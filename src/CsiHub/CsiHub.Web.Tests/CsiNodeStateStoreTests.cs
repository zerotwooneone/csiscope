using System.Diagnostics;
using CsiHub.Features.Home.Services;
using CsiHub.Ingestion;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using CsiHub.Ingestion.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiHub.Web.Tests;

public sealed class CsiNodeStateStoreTests
{
    [Fact]
    public async Task SyncDiag_Preserved_While_In_DiagSync()
    {
        using var cts = new CancellationTokenSource();
        var (store, channel) = CreateStore();
        await store.StartAsync(cts.Token);

        var mac = "TEST:00:11:22";
        var port = "COM_TEST";

        var diagPayload = new NodePayload
        {
            Type = "diag",
            Test = "sync",
            Mac = mac,
            PortName = port,
            SyncDiag = new SyncDiagnosticMetrics
            {
                PulseCount = 42,
                LatencyUs = 1.2,
                JitterUs = 0.3,
            },
        };

        Assert.True(channel.TryPublish(diagPayload));

        var heartbeat = new NodeStateChanged(
            port,
            mac,
            NodeConnectionState.DiagSync,
            DateTimeOffset.UtcNow,
            Uptime: 1,
            ClockLeader: false,
            Bandwidth: 20);

        Assert.True(channel.TryPublishState(heartbeat));

        await WaitForAsync(
            () => store.Nodes.TryGetValue(mac, out var n) && n.SyncDiag is not null,
            TimeSpan.FromSeconds(1));

        Assert.True(store.Nodes.TryGetValue(mac, out var node));
        Assert.NotNull(node.SyncDiag);
        Assert.Equal(42, node.SyncDiag.PulseCount);
        Assert.Equal(NodeConnectionState.DiagSync, node.State);

        await store.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncDiag_Cleared_When_Leaving_DiagSync()
    {
        using var cts = new CancellationTokenSource();
        var (store, channel) = CreateStore();
        await store.StartAsync(cts.Token);

        var mac = "TEST:00:11:22";
        var port = "COM_TEST";

        var diagPayload = new NodePayload
        {
            Type = "diag",
            Test = "sync",
            Mac = mac,
            PortName = port,
            SyncDiag = new SyncDiagnosticMetrics
            {
                PulseCount = 5,
                LatencyUs = 0.1,
                JitterUs = 0.0,
            },
        };

        channel.TryPublish(diagPayload);

        var heartbeatInDiag = new NodeStateChanged(
            port,
            mac,
            NodeConnectionState.DiagSync,
            DateTimeOffset.UtcNow,
            Uptime: 1,
            ClockLeader: false,
            Bandwidth: 20);

        channel.TryPublishState(heartbeatInDiag);

        await WaitForAsync(
            () => store.Nodes.TryGetValue(mac, out var n) && n.SyncDiag is not null,
            TimeSpan.FromSeconds(1));

        var heartbeatStandby = new NodeStateChanged(
            port,
            mac,
            NodeConnectionState.Standby,
            DateTimeOffset.UtcNow,
            Uptime: 2);

        channel.TryPublishState(heartbeatStandby);

        await WaitForAsync(
            () => store.Nodes.TryGetValue(mac, out var n) && n.State == NodeConnectionState.Standby,
            TimeSpan.FromSeconds(1));

        Assert.True(store.Nodes.TryGetValue(mac, out var node));
        Assert.Null(node.SyncDiag);

        await store.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RfScan_Preserved_After_State_Update()
    {
        using var cts = new CancellationTokenSource();
        var (store, channel) = CreateStore();
        await store.StartAsync(cts.Token);

        var mac = "TEST:00:11:22";
        var port = "COM_TEST";

        var rfPayload = new NodePayload
        {
            Type = "rf_scan",
            Mac = mac,
            PortName = port,
            Rf = new RfChannelMetrics
            {
                Channel = 6,
                RssiMin = -80,
                RssiMax = -30,
                RssiAvg = -55,
                Packets = 100,
                Errors = 0,
                DurationMs = 250,
            },
        };

        channel.TryPublish(rfPayload);

        var heartbeat = new NodeStateChanged(
            port,
            mac,
            NodeConnectionState.Standby,
            DateTimeOffset.UtcNow,
            Uptime: 1);

        channel.TryPublishState(heartbeat);

        await WaitForAsync(
            () => store.Nodes.TryGetValue(mac, out var n) && n.RfScan.Count > 0,
            TimeSpan.FromSeconds(1));

        Assert.True(store.Nodes.TryGetValue(mac, out var node));
        Assert.Single(node.RfScan);
        Assert.True(node.RfScan.ContainsKey(6));

        await store.StopAsync(CancellationToken.None);
    }

    private static (CsiNodeStateStore Store, CsiIngestionChannel Channel) CreateStore()
    {
        var options = Options.Create(new CsiIngestionOptions());
        var channel = new CsiIngestionChannel(options);
        var portManager = new CsiNodePortManager(
            options,
            channel,
            new ThrowingSerialPortFactory(),
            NullLogger<CsiNodePortManager>.Instance,
            Array.Empty<IPayloadHandler>());

        var store = new CsiNodeStateStore(
            channel,
            portManager,
            new CsiNodeConfigurationService(),
            new RfChannelEvaluator(),
            NullLogger<CsiNodeStateStore>.Instance);

        return (store, channel);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < timeout)
        {
            await Task.Delay(20);
        }

        Assert.True(predicate(), "Expected condition was not satisfied in time.");
    }

    private sealed class ThrowingSerialPortFactory : ISerialPortFactory
    {
        public ISerialPort Create(string portName, int baudRate)
            => throw new InvalidOperationException(
                $"Serial port factory should not be called in state store tests (port {portName}).");
    }
}
