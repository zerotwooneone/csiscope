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

}
