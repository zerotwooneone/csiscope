using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using CsiScope.Domain.Model;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CsiScope.Infrastructure.Tests;

[Trait("Category", "Component")] // real send loop + timing coordination — not pure unit tests
public class NodeSerialAdapterTests
{
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");

    private static (NodeSerialAdapter Adapter, ConcurrentQueue<byte[]> Sent) Create(TimeSpan? ackTimeout = null)
    {
        var sent = new ConcurrentQueue<byte[]>();
        var adapter = new NodeSerialAdapter(
            (bytes, _) =>
            {
                sent.Enqueue(bytes.ToArray());
                return ValueTask.CompletedTask;
            },
            ackTimeout: ackTimeout ?? TimeSpan.FromMilliseconds(100));
        return (adapter, sent);
    }

    private static string SentJson(byte[] frame)
    {
        var accum = frame.ToList();
        string? json = null;
        SerialFrameCodec.DrainFrames(accum, p => json ??= Encoding.UTF8.GetString(p));
        return json!;
    }

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

        // Assert — passive schema: ch, bw, mode, seq, mac_filter, binary-framed.
        var json = SentJson(sent.First());
        json.Should().Contain("\"cmd\":\"set_rf\"");
        json.Should().Contain("\"ch\":6");
        json.Should().Contain("\"bw\":20");
        json.Should().Contain("\"mode\":\"passive\"");
        json.Should().Contain("\"mac_filter\":[\"08E9F6639ACC\"]");
        json.Should().Contain("\"seq\":");
        sent.First()[0].Should().Be(SerialFrameCodec.MagicHigh);
        sent.First()[1].Should().Be(SerialFrameCodec.MagicLow);

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
        var json = SentJson(sent.First());
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

    #region Virtual Time — deterministic timeout control via FakeTimeProvider

    [Fact]
    public async Task Timeout_retries_then_fails_on_virtual_clock()
    {
        // Arrange — virtual clock: timeouts fire only when the test advances it.
        var time = new FakeTimeProvider();
        var sent = new ConcurrentQueue<byte[]>();
        var adapter = new NodeSerialAdapter(
            (bytes, _) => { sent.Enqueue(bytes.ToArray()); return ValueTask.CompletedTask; },
            time: time,
            ackTimeout: TimeSpan.FromSeconds(1));
        await using var _ = adapter;

        // Act — first write lands; no ack; advance past the timeout -> retry.
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => sent.Count == 2);
        time.Advance(TimeSpan.FromSeconds(1));

        // Assert — two attempts, then false. No real time elapsed.
        (await pending).Should().BeFalse();
        sent.Count.Should().Be(2);
    }

    [Fact]
    public async Task Ack_before_virtual_deadline_completes_true()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var sent = new ConcurrentQueue<byte[]>();
        var adapter = new NodeSerialAdapter(
            (bytes, _) => { sent.Enqueue(bytes.ToArray()); return ValueTask.CompletedTask; },
            time: time,
            ackTimeout: TimeSpan.FromSeconds(1));
        await using var _ = adapter;

        // Act — ack arrives before the clock advances.
        var pending = adapter.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 1);
        adapter.NotifyAck(1, success: true);

        // Assert — completed true; advancing the clock changes nothing.
        (await pending).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(10));
        sent.Count.Should().Be(1); // no retry fired
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
