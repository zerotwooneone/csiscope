using System.Collections.Immutable;
using CsiScope.Domain.Model;
using FluentAssertions;
using Xunit;

namespace CsiScope.Infrastructure.Tests;

public class BroadcastRadioAdapterTests
{
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");

    /// <summary>A node adapter that records writes; acks are driven explicitly by the test.</summary>
    private static NodeSerialAdapter RecordingNode(List<byte[]> sent)
        => new(
            (bytes, _) =>
            {
                sent.Add(bytes.ToArray());
                return ValueTask.CompletedTask;
            },
            ackTimeout: TimeSpan.FromMilliseconds(50));

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue("timed out waiting for condition");
    }

    [Fact]
    public async Task Broadcast_fans_out_to_every_registered_node()
    {
        // Arrange
        var broadcast = new BroadcastRadioAdapter();
        var sentA = new List<byte[]>();
        var sentB = new List<byte[]>();
        await using var a = RecordingNode(sentA);
        await using var b = RecordingNode(sentB);
        broadcast.RegisterNode("COM9", a);
        broadcast.RegisterNode("COM10", b);

        // Act — fire, wait for both writes, then ack both ports.
        var pending = broadcast.BroadcastSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sentA.Count == 1 && sentB.Count == 1);
        broadcast.NotifyAck("COM9", 1, success: true);
        broadcast.NotifyAck("COM10", 1, success: true);

        // Assert
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task Majority_quorum_succeeds_when_one_node_fails()
    {
        // Arrange — two will ack, one never does (times out at 50ms).
        var broadcast = new BroadcastRadioAdapter();
        var sent = new List<byte[]>();
        await using var a = RecordingNode(sent);
        await using var b = RecordingNode(sent);
        await using var dead = RecordingNode(sent);
        broadcast.RegisterNode("COM9", a);
        broadcast.RegisterNode("COM10", b);
        broadcast.RegisterNode("COM11", dead);

        // Act
        var pending = broadcast.BroadcastSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 3);
        broadcast.NotifyAck("COM9", 1, success: true);
        broadcast.NotifyAck("COM10", 1, success: true);

        // Assert — 2 of 3 acked: quorum reached despite the dead node.
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task Quorum_fails_when_majority_of_nodes_are_dead()
    {
        // Arrange — only one of three acks.
        var broadcast = new BroadcastRadioAdapter();
        var sent = new List<byte[]>();
        await using var a = RecordingNode(sent);
        await using var dead1 = RecordingNode(sent);
        await using var dead2 = RecordingNode(sent);
        broadcast.RegisterNode("COM9", a);
        broadcast.RegisterNode("COM10", dead1);
        broadcast.RegisterNode("COM11", dead2);

        // Act
        var pending = broadcast.BroadcastSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sent.Count == 3);
        broadcast.NotifyAck("COM9", 1, success: true);

        // Assert — 1 of 3 is not a quorum.
        (await pending).Should().BeFalse();
    }

    [Fact]
    public async Task NotifyAck_routes_to_the_owning_port_only()
    {
        // Arrange — two nodes; only COM10's pending command should complete.
        var broadcast = new BroadcastRadioAdapter();
        var sentA = new List<byte[]>();
        var sentB = new List<byte[]>();
        await using var a = RecordingNode(sentA);
        await using var b = RecordingNode(sentB);
        broadcast.RegisterNode("COM9", a);
        broadcast.RegisterNode("COM10", b);

        // Act — both send seq 1; only COM10's ack arrives.
        var pendingA = a.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        var pendingB = b.SendSetRfAsync(Ch6, ImmutableArray.Create(Target));
        await WaitFor(() => sentA.Count == 1 && sentB.Count == 1);
        broadcast.NotifyAck("COM10", 1, success: true);

        // Assert — per-port seq spaces: COM9's seq-1 is untouched by COM10's ack.
        (await pendingB).Should().BeTrue();
        (await pendingA).Should().BeFalse(); // timed out — no cross-completion
    }

    [Fact]
    public async Task Broadcast_with_no_nodes_returns_false()
    {
        // Act & Assert
        (await new BroadcastRadioAdapter().BroadcastSetRfAsync(Ch6, ImmutableArray.Create(Target)))
            .Should().BeFalse();
    }
}
