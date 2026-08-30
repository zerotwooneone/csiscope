using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.IntegrationTests.Fakes;
using CsiHub.Ingestion.Models;
using CsiHub.Ingestion.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CsiHub.Ingestion.IntegrationTests;

/// <summary>
/// Verifies the full DI graph and hosted-service lifecycle: the background service,
/// CsiNodePortManager, SerialPipelineReader, and CsiIngestionChannel are wired together
/// exactly as they will be in production.
/// </summary>
public class CsiIngestionBackgroundServiceTests
{
    [Fact]
    public async Task Hosted_Service_Ingests_Heartbeat_Through_Channels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var portFactory = new FakeSerialPortFactory();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddCsiIngestion(options =>
                {
                    options.SerialPortNames = new List<string> { "COM9" };
                    options.SerialBaudRate = 921600;
                    options.ReconnectDelayMs = 2000;
                    options.PayloadChannelCapacity = 16;
                    options.StateChannelCapacity = 16;
                    options.CommandChannelCapacity = 16;
                });
                services.AddSingleton<ISerialPortFactory>(portFactory);
            })
            .Build();

        await host.StartAsync(cts.Token);

        var port = portFactory.GetOrCreate("COM9");
        await TestHelper.WaitForOpenAsync(port).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        await TestHelper.WriteLineAsync(port.Downlink, """{"type":"hb","mac":"00:11:22:33:44:55","state":"standby","uptime":5}""");

        var channel = host.Services.GetRequiredService<CsiIngestionChannel>();

        var payload = await channel.PayloadReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

        Assert.Equal("hb", payload.Type);
        Assert.Equal("standby", payload.State);

        var state = await channel.StateReader.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

        Assert.Equal("COM9", state.PortName);
        Assert.Equal(NodeConnectionState.Standby, state.State);

        await host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Hosted_Service_Handles_Production_Rate_Burst()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var portFactory = new FakeSerialPortFactory();

        // Use production-like channel capacities so the test exercises backpressure
        // and DropOldest behavior at the 50-100 Hz scale the firmware targets.
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddCsiIngestion(options =>
                {
                    options.SerialPortNames = new List<string> { "COM9" };
                    options.SerialBaudRate = 921600;
                    options.ReconnectDelayMs = 2000;
                    options.PayloadChannelCapacity = 1000;
                    options.StateChannelCapacity = 256;
                    options.CommandChannelCapacity = 32;
                });
                services.AddSingleton<ISerialPortFactory>(portFactory);
            })
            .Build();

        await host.StartAsync(cts.Token);

        var port = portFactory.GetOrCreate("COM9");
        await TestHelper.WaitForOpenAsync(port).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

        const int capacity = 1000;

        for (int i = 0; i < capacity; i++)
        {
            var json = $@"{{""type"":""csi"",""t"":{i},""c"":[1.0,2.0,3.0]}}";
            await TestHelper.WriteLineAsync(port.Downlink, json);
        }

        var channel = host.Services.GetRequiredService<CsiIngestionChannel>();

        var payloads = new List<NodePayload>();
        for (int i = 0; i < capacity; i++)
        {
            var payload = await channel.PayloadReader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            payloads.Add(payload);
        }

        // The production channel has room for the full burst and must not block or drop.
        Assert.Equal(capacity, payloads.Count);
        Assert.Equal(0, payloads[0].Timestamp);
        Assert.Equal(capacity - 1, payloads[^1].Timestamp);

        await host.StopAsync(cts.Token);
    }
}
