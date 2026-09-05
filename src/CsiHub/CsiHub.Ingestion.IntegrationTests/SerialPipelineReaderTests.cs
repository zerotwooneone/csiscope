using System.Text;
using System.Text.Json;
using CsiHub.Ingestion.Models;
using CsiHub.Ingestion.Pipelines;

namespace CsiHub.Ingestion.IntegrationTests;

public class SerialPipelineReaderTests
{
    private static async Task<string?> ReadFrameWithTimeoutAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var readTask = TestHelper.ReadFrameAsync(stream, cts.Token);
            await readTask.WaitAsync(timeout, cts.Token).ConfigureAwait(false);
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Parses_Config_And_Heartbeat_And_Publishes_State()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"hb","mac":"00:11:22:33:44:55","state":"standby","uptime":5}""");

        var state = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("COM9", state.PortName);
        Assert.Equal(NodeConnectionState.Standby, state.State);
        Assert.Equal("00:11:22:33:44:55", state.Mac);
    }

    [Fact]
    public async Task TrySendCommand_Writes_Framed_Command()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        Assert.True(harness.Reader.TrySendCommand("""{"cmd":"get_config"}"""));

        string? response = await ReadFrameWithTimeoutAsync(harness.Port.Uplink, TimeSpan.FromSeconds(1));
        Assert.Equal("{\"cmd\":\"get_config\"}", response);
    }

    [Fact]
    public async Task SendCommandAsync_Waits_For_Matching_Ack()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");

        var commandTask = harness.Reader.SendCommandAsync("""{"cmd":"set_rf","ch":1}""", 500, 3);

        string? commandJson = await ReadFrameWithTimeoutAsync(harness.Port.Uplink, TimeSpan.FromSeconds(1));
        Assert.NotNull(commandJson);

        using var commandDoc = JsonDocument.Parse(commandJson!);
        Assert.True(commandDoc.RootElement.TryGetProperty("seq", out var seqProp));
        int seq = seqProp.GetInt32();

        await TestHelper.WriteFrameAsync(
            harness.Port.Downlink,
            $"{{\"type\":\"ack\",\"cmd\":\"set_rf\",\"success\":true,\"seq\":{seq},\"state\":\"streaming\"}}");

        await commandTask.WaitAsync(TimeSpan.FromSeconds(2));

        Ack? ack = await commandTask;

        Assert.NotNull(ack);
        Assert.Equal(seq, ack!.Seq);
        Assert.Equal("set_rf", ack.Cmd);
        Assert.True(ack.Success);
    }

    [Fact]
    public async Task Cancellation_Maps_To_Disconnected_State()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");

        var firstState = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NodeConnectionState.Standby, firstState.State);

        // Simulate a host shutdown by cancelling the reader's token.
        await harness.DisposeAsync();

        var disconnected = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NodeConnectionState.Disconnected, disconnected.State);
    }

    [Fact]
    public async Task RfScan_Frame_Is_Published_To_StateStore()
    {
        await using var harness = new SerialPipelineTestHarness("COM10");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");

        // Drain the standby state published when config is ingested.
        await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        const string rfScanJson = """{"type":"rf_scan","mac":"00:11:22:33:44:55","ch":6,"rssi_min":-80,"rssi_max":-40,"rssi_avg":-62.5,"packets":42,"errors":3,"duration_ms":500,"timestamp":12345,"top_macs":[{"mac":"11:22:33:44:55:66","packets":30,"errors":1,"rssi_min":-70,"rssi_max":-50,"rssi_avg":-60.0},{"mac":"AA:BB:CC:DD:EE:FF","packets":12,"errors":2,"rssi_min":-80,"rssi_max":-60,"rssi_avg":-72.5}]}""";
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, rfScanJson);

        var payload = await harness.Channel.StateStorePayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("rf_scan", payload.Type);
        Assert.NotNull(payload.Rf);
        Assert.Equal(6, payload.Rf.Channel);
    }

    [Fact]
    public async Task Imu_Frame_Is_Published_With_Quaternion()
    {
        await using var harness = new SerialPipelineTestHarness("COM11");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"imu","mac":"00:11:22:33:44:55","t":12345,"qw":1.0,"qx":0.1,"qy":-0.2,"qz":0.3}""");

        var payload = await harness.Channel.StateStorePayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("imu", payload.Type);
        Assert.Equal("00:11:22:33:44:55", payload.Mac);
        // Quaternion fields are stored in [w, x, y, z] order.
        Assert.Equal(new[] { 1.0, 0.1, -0.2, 0.3 }, payload.Imu);
    }

    [Fact]
    public async Task Boot_Frame_Is_Published_And_Maps_To_Standby()
    {
        await using var harness = new SerialPipelineTestHarness("COM12");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"streaming","baud":921600,"bw":20,"version":"0.1.0"}""");

        // Drain the streaming state published when config is ingested.
        var configState = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(NodeConnectionState.Streaming, configState.State);

        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"boot","mac":"00:11:22:33:44:55","state":"boot"}""");

        var payload = await harness.Channel.StateStorePayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("boot", payload.Type);

        var bootState = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(NodeConnectionState.Standby, bootState.State);
    }

    [Fact]
    public async Task Post_Frame_Is_Ignored()
    {
        await using var harness = new SerialPipelineTestHarness("COM13");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"post","mac":"00:11:22:33:44:55","imu":true}""");

        bool received;
        try
        {
            received = await harness.Channel.StateStorePayloadReader
                .WaitToReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(300));
        }
        catch (TimeoutException)
        {
            received = false;
        }

        Assert.False(received);
    }

    [Fact]
    public async Task Diag_Sync_Frame_Is_Published_With_Metrics()
    {
        await using var harness = new SerialPipelineTestHarness("COM14");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"bw":20,"version":"0.1.0"}""");
        await TestHelper.WriteFrameAsync(harness.Port.Downlink, """{"type":"diag","test":"sync","mac":"00:11:22:33:44:55","pulse_count":42,"latency_us":1.25,"jitter_us":0.5}""");

        var payload = await harness.Channel.StateStorePayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("diag", payload.Type);
        Assert.Equal("sync", payload.Test);
        Assert.NotNull(payload.SyncDiag);
        Assert.Equal(42, payload.SyncDiag.PulseCount);
        Assert.Equal(1.25, payload.SyncDiag.LatencyUs);
        Assert.Equal(0.5, payload.SyncDiag.JitterUs);
    }

}
