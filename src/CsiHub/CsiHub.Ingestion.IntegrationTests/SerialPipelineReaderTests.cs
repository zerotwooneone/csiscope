using System.Text;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.IntegrationTests.Fakes;
using CsiHub.Ingestion.Models;
using CsiHub.Ingestion.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion.IntegrationTests;

public class SerialPipelineReaderTests
{
    private static CsiIngestionChannel CreateChannel() =>
        new(Options.Create(new CsiIngestionOptions
        {
            PayloadChannelCapacity = 16,
            StateChannelCapacity = 16,
            CommandChannelCapacity = 16
        }));

    private static SerialPipelineReader CreateReader(FakeSerialPort port, CsiIngestionChannel channel) =>
        new(
            port.PortName,
            () => port,
            commandChannelCapacity: 16,
            reconnectDelayMs: 2000,
            channel,
            NullLogger<SerialPipelineReader>.Instance);

    private static async Task WriteLineAsync(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(bytes.AsMemory());
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        return await reader.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private static async Task WaitForOpenAsync(FakeSerialPort port)
    {
        while (!port.IsOpen)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Parses_Heartbeat_And_Publishes_Payload_And_State()
    {
        var channel = CreateChannel();
        var port = new FakeSerialPort("COM9");
        var reader = CreateReader(port, channel);
        using var cts = new CancellationTokenSource();

        var runTask = Task.Run(() => reader.RunAsync(cts.Token));

        await WaitForOpenAsync(port);
        await WriteLineAsync(port.Downlink, """{"type":"hb","mac":"00:11:22:33:44:55","state":"standby","uptime":5}""");

        var payload = await channel.PayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("hb", payload.Type);
        Assert.Equal("standby", payload.State);

        var state = await channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("COM9", state.PortName);
        Assert.Equal(NodeConnectionState.Standby, state.State);

        cts.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task TrySendCommand_Writes_Ndjson_Command()
    {
        var channel = CreateChannel();
        var port = new FakeSerialPort("COM9");
        var reader = CreateReader(port, channel);
        using var cts = new CancellationTokenSource();

        var runTask = Task.Run(() => reader.RunAsync(cts.Token));

        await WaitForOpenAsync(port);
        Assert.True(reader.TrySendCommand("""{"cmd":"get_config"}"""));

        using var uplinkReader = new StreamReader(port.Uplink, Encoding.UTF8, false, 1024, leaveOpen: true);
        string? response = await ReadWithTimeoutAsync(uplinkReader, TimeSpan.FromSeconds(1));
        Assert.Equal("{\"cmd\":\"get_config\"}", response);

        cts.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_Maps_To_Disconnected_State()
    {
        var channel = CreateChannel();
        var port = new FakeSerialPort("COM9");
        var reader = CreateReader(port, channel);
        using var cts = new CancellationTokenSource();

        var runTask = Task.Run(() => reader.RunAsync(cts.Token));

        await WaitForOpenAsync(port);
        await WriteLineAsync(port.Downlink, """{"type":"hb","state":"standby"}""");

        var firstState = await channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NodeConnectionState.Standby, firstState.State);

        cts.Cancel();

        var disconnected = await channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NodeConnectionState.Disconnected, disconnected.State);

        await runTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
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
