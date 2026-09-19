using System.Collections.Immutable;
using System.Text;
using CsiScope.Domain.Model;
using FluentAssertions;
using Xunit;

namespace CsiScope.Infrastructure.IntegrationTests;

/// <summary>
/// End-to-end worker tests over a loopback serial stream — the test plays the
/// firmware node: writing NDJSON in, reading host commands out.
/// </summary>
[Trait("Category", "Integration")]
public class SensingHostWorkerTests
{
    // src = 9654321234567 (0x08C9…) — globally-administered unicast, passes the gatekeeper.
    private const string CsiLine =
        """{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":9654321234567,"ch":6,"seq":7,"rssi":-55,"t":12345,"c":[3,4]}""";

    [Fact]
    public async Task Csi_frame_flows_through_to_orchestrator_baseline()
    {
        // Arrange
        await using var harness = new WorkerTestHarness();
        await harness.StartAsync();

        // Act — node emits one csi frame.
        await harness.WriteFrameAsync(CsiLine);

        // Assert — parsed, dispatched, baseline created on the writer thread.
        await WorkerTestHarness.WaitForAsync(() => harness.Orchestrator.TrackedBaselineCount == 1);
        await WorkerTestHarness.WaitForAsync(() => harness.Worker.LatestSnapshot is not null);
        harness.Worker.LatestSnapshot!.Baselines.Should().HaveCount(1);
    }

    [Fact]
    public async Task Config_frame_registers_expected_node()
    {
        // Arrange
        await using var harness = new WorkerTestHarness();
        await harness.StartAsync();

        // Act — node announces itself.
        await harness.WriteFrameAsync(
            """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"version":"0.1.0"}""");

        // Assert — roster updated via the consumer loop.
        await WorkerTestHarness.WaitForAsync(() => harness.Orchestrator.ExpectedNodeCount == 1);
    }

    [Fact]
    public async Task Heartbeat_registers_node_liveness()
    {
        // Arrange
        await using var harness = new WorkerTestHarness();
        await harness.StartAsync();

        // Act
        await harness.WriteFrameAsync(
            """{"type":"hb","mac":"14:C1:9F:2E:53:D0","state":"standby","uptime":5}""");

        // Assert
        await WorkerTestHarness.WaitForAsync(() => harness.Orchestrator.ExpectedNodeCount == 1);
    }

    [Fact]
    public async Task Command_egress_writes_set_rf_and_ack_completes_quorum()
    {
        // Arrange — wait for the port's adapter to register with the composite.
        await using var harness = new WorkerTestHarness();
        await harness.StartAsync();
        await WorkerTestHarness.WaitForAsync(() => harness.Radio.NodeCount == 1);

        // Act — broadcast a hop; the node sees the wire format and acks it.
        var pending = harness.Radio.BroadcastSetRfAsync(
            new WifiChannel(6),
            ImmutableArray.Create(MacAddress.Parse("08:E9:F6:63:9A:CC")));

        var commandJson = await harness.ReadFrameAsync(TimeSpan.FromSeconds(2));
        commandJson.Should().Contain("\"cmd\":\"set_rf\"").And.Contain("\"seq\":1");

        await harness.WriteFrameAsync(
            """{"type":"ack","cmd":"set_rf","success":true,"seq":1,"state":"streaming"}""");

        // Assert — ack routed back through the port tag to the pending command.
        (await pending).Should().BeTrue();
    }

    [Fact]
    public async Task Torn_frames_still_parse()
    {
        // Arrange
        await using var harness = new WorkerTestHarness();
        await harness.StartAsync();

        // Act — split the encoded frame mid-payload across two writes.
        var frame = SerialFrameCodec.EncodeFrame(Encoding.UTF8.GetBytes(CsiLine));
        var split = frame.Length / 2;
        await harness.WriteRawAsync(frame[..split]);
        await Task.Delay(50);                                     // separate read chunk
        await harness.WriteRawAsync(frame[split..]);

        // Assert — the decoder reassembled the frame; baseline exists.
        await WorkerTestHarness.WaitForAsync(() => harness.Orchestrator.TrackedBaselineCount == 1);
    }
}
