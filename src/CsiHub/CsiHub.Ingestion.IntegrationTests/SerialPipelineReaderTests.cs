using System.Text;
using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.IntegrationTests;

public class SerialPipelineReaderTests
{
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        return await reader.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    [Fact]
    public async Task Parses_Heartbeat_And_Publishes_Payload_And_State()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteLineAsync(harness.Port.Downlink, """{"type":"hb","mac":"00:11:22:33:44:55","state":"standby","uptime":5}""");

        var payload = await harness.Channel.PayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("hb", payload.Type);
        Assert.Equal("standby", payload.State);

        var state = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("COM9", state.PortName);
        Assert.Equal(NodeConnectionState.Standby, state.State);
    }

    [Fact]
    public async Task TrySendCommand_Writes_Ndjson_Command()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        Assert.True(harness.Reader.TrySendCommand("""{"cmd":"get_config"}"""));

        using var uplinkReader = new StreamReader(harness.Port.Uplink, Encoding.UTF8, false, 1024, leaveOpen: true);
        string? response = await ReadWithTimeoutAsync(uplinkReader, TimeSpan.FromSeconds(1));
        Assert.Equal("{\"cmd\":\"get_config\"}", response);
    }

    [Fact]
    public async Task Cancellation_Maps_To_Disconnected_State()
    {
        await using var harness = new SerialPipelineTestHarness("COM9");

        await TestHelper.WaitForOpenAsync(harness.Port);
        await TestHelper.WriteLineAsync(harness.Port.Downlink, """{"type":"hb","state":"standby"}""");

        var firstState = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NodeConnectionState.Standby, firstState.State);

        // Simulate a host shutdown by cancelling the reader's token.
        await harness.DisposeAsync();

        var disconnected = await harness.Channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NodeConnectionState.Disconnected, disconnected.State);
    }

    private static async Task<string?> ReadWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await ReadLineAsync(reader, cts.Token);
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
}
