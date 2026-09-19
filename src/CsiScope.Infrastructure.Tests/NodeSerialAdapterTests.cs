using System.Collections.Immutable;
using System.Text;
using CsiScope.Domain.Model;
using FluentAssertions;
using Xunit;

namespace CsiScope.Infrastructure.Tests;

public class NodeSerialAdapterTests
{
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");

    private static (NodeSerialAdapter Adapter, List<byte[]> Sent) Create(TimeSpan? ackTimeout = null)
    {
        var sent = new List<byte[]>();
        var adapter = new NodeSerialAdapter(
            (bytes, _) =>
            {
                sent.Add(bytes.ToArray());
                return ValueTask.CompletedTask;
            },
            ackTimeout ?? TimeSpan.FromMilliseconds(100));
        return (adapter, sent);
    }

    private static string SentJson(byte[] frame) => Encoding.UTF8.GetString(frame).TrimEnd('\n');

    #region Wire Format

    [Fact]
    public async Task Passive_command_emits_firmware_schema_with_seq()
    {
        // Arrange
        var (adapter, sent) = Create();
        await using var _ = adapter;

        // Act
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 1);

        // Assert — passive schema: ch, bw, mode, seq, mac_filter, newline-framed.
        var json = SentJson(sent[0]);
        json.Should().Contain("\"cmd\":\"set_rf\"");
        json.Should().Contain("\"ch\":6");
        json.Should().Contain("\"bw\":20");
        json.Should().Contain("\"mode\":\"passive\"");
        json.Should().Contain("\"mac_filter\":[\"08E9F6639ACC\"]");
        json.Should().Contain("\"seq\":");
        sent[0][^1].Should().Be((byte)'\n');

        adapter.NotifyAck(1, true);
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_filter_emits_scan_variant_not_passive()
    {
        // Arrange — firmware rejects empty mac_filter in passive mode.
        var (adapter, sent) = Create();
        await using var _ = adapter;

        // Act
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray<MacAddress>.Empty);
        await WaitFor(() => sent.Count == 1);

        // Assert — dwell_ms scan variant; no mac_filter key at all.
        var json = SentJson(sent[0]);
        json.Should().Contain("\"dwell_ms\"");
        json.Should().NotContain("mac_filter");
        json.Should().NotContain("\"mode\":\"passive\"");

        adapter.NotifyAck(1, true);
        (await pending).Should().BeTrue();
    }

    #endregion

    #region ACK Tracking

    [Fact]
    public async Task Command_completes_true_on_matching_ack()
    {
        // Arrange
        var (adapter, sent) = Create();
        await using var _ = adapter;

        // Act
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 1);
        adapter.NotifyAck(1, success: true);

        // Assert
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task Command_returns_false_after_timeout_and_retry()
    {
        // Arrange — no ack ever arrives; short timeout, 2 attempts.
        var (adapter, sent) = Create(ackTimeout: TimeSpan.FromMilliseconds(50));
        await using var _ = adapter;

        // Act
        var result = await adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));

        // Assert — two wire writes (initial + one retry), then false.
        result.Should().BeFalse();
        sent.Count.Should().Be(2);
    }

    [Fact]
    public async Task Nack_fails_fast_without_retry()
    {
        // Arrange — firmware rejects the command (success:false).
        var (adapter, sent) = Create(ackTimeout: TimeSpan.FromMilliseconds(50));
        await using var _ = adapter;

        // Act
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 1);
        adapter.NotifyAck(1, success: false);

        // Assert — one write only; a NACK is terminal, not retried.
        (await pending).Should().BeFalse();
        sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task Retry_succeeds_when_late_ack_arrives()
    {
        // Arrange — first attempt times out; ack arrives during the retry.
        var (adapter, sent) = Create(ackTimeout: TimeSpan.FromMilliseconds(50));
        await using var _ = adapter;

        // Act
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 2); // retried
        adapter.NotifyAck(1, success: true);

        // Assert
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task Ack_with_wrong_seq_does_not_complete_command()
    {
        // Arrange
        var (adapter, sent) = Create(ackTimeout: TimeSpan.FromMilliseconds(50));
        await using var _ = adapter;

        // Act
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 1);
        adapter.NotifyAck(999, success: true); // stale seq

        // Assert — still pending; eventually times out false.
        (await pending).Should().BeFalse();
    }

    #endregion

    #region Queue Safety

    [Fact]
    public async Task Evicted_command_completes_false_instead_of_hanging()
    {
        // Arrange — a writer that blocks until released so the queue backs up.
        var gate = new TaskCompletionSource();
        var adapter = new NodeSerialAdapter(
            (_, _) => new ValueTask(gate.Task),
            ackTimeout: TimeSpan.FromMilliseconds(10));
        await using var _ = adapter;

        // Act — fill the queue (32) plus overflow; the first send blocks the loop.
        var tasks = new List<Task<bool>>();
        for (var i = 0; i < 40; i++)
        {
            tasks.Add(adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target)));
        }

        // Evictions are synchronous — at least 7 commands already completed false.
        tasks.Count(t => t.IsCompleted).Should().BeGreaterThanOrEqualTo(7);

        // Release the write so the loop drains (each command times out fast).
        gate.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert — every command completed; none hung, none acked.
        results.Should().OnlyContain(r => r == false);
    }

    [Fact]
    public async Task Write_exception_fails_command_but_keeps_loop_alive()
    {
        // Arrange — first write throws, second succeeds.
        var calls = 0;
        var adapter = new NodeSerialAdapter(
            (_, _) => ++calls == 1
                ? throw new IOException("port gone")
                : ValueTask.CompletedTask,
            ackTimeout: TimeSpan.FromMilliseconds(50));
        await using var _ = adapter;

        // Act
        var first = await adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        var second = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => calls == 2);
        adapter.NotifyAck(2, success: true);

        // Assert — the loop survived the transport failure.
        first.Should().BeFalse();
        (await second).Should().BeTrue();
    }

    #endregion

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue("timed out waiting for condition");
    }
}
