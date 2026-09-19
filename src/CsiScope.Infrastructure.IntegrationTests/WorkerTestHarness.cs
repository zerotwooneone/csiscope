using System.Collections.Immutable;
using System.Text;
using CsiScope.Application;
using CsiScope.Domain.Model;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CsiScope.Infrastructure.IntegrationTests;

/// <summary>
/// Self-cleaning harness for <see cref="SensingHostWorker"/> integration tests.
/// Wires a real orchestrator, radio composite, anomaly sink, and worker over a
/// loopback "serial port" — the test plays the firmware node on the host side.
/// </summary>
public sealed class WorkerTestHarness : IAsyncDisposable
{
    private readonly LoopbackSerialStream _port = new();
    private readonly CancellationTokenSource _cts = new();

    public WorkerTestHarness()
    {
        Time = new FakeTimeProvider();
        Radio = new BroadcastRadioAdapter();
        Sink = new ChannelAnomalySink();
        Orchestrator = new SensingOrchestrator(
            Radio, Sink, expectedNodes: ImmutableArray<MacAddress>.Empty);

        Worker = new SensingHostWorker(
            Orchestrator,
            Radio,
            Time,
            openStream: (_, _) => new ValueTask<Stream>(_port.DeviceSide),
            tickInterval: TimeSpan.FromMilliseconds(50),
            reconnectDelay: TimeSpan.FromMilliseconds(50),
            ackTimeout: TimeSpan.FromMilliseconds(200));
    }

    public FakeTimeProvider Time { get; }
    public BroadcastRadioAdapter Radio { get; }
    public ChannelAnomalySink Sink { get; }
    public SensingOrchestrator Orchestrator { get; }
    public SensingHostWorker Worker { get; }

    /// <summary>Test side of the wire — write node NDJSON, read host commands.</summary>
    public Stream Node => _port.HostSide;

    /// <summary>Starts the worker and activates a session on the loopback port.</summary>
    public async Task StartAsync()
    {
        await Worker.StartAsync(_cts.Token);
        Worker.StartSensing(new[] { "LOOPBACK1" });
    }

    /// <summary>Writes one framed JSON payload as the firmware node would.</summary>
    public async Task WriteFrameAsync(string json)
    {
        var frame = SerialFrameCodec.EncodeFrame(Encoding.UTF8.GetBytes(json));
        await Node.WriteAsync(frame);
        await Node.FlushAsync();
    }

    /// <summary>Writes raw bytes — for partial/torn frame tests.</summary>
    public async Task WriteRawAsync(byte[] bytes)
    {
        await Node.WriteAsync(bytes);
        await Node.FlushAsync();
    }

    /// <summary>Reads one framed command the host wrote to the node; returns the JSON payload.</summary>
    public async Task<string> ReadFrameAsync(TimeSpan timeout)
    {
        var accum = new List<byte>();
        var buf = new byte[4096];
        string? result = null;
        using var cts = new CancellationTokenSource(timeout);
        while (result is null)
        {
            var read = await Node.ReadAsync(buf, cts.Token);
            accum.AddRange(buf.Take(read));
            SerialFrameCodec.DrainFrames(accum, p => result ??= Encoding.UTF8.GetString(p));
        }

        return result;
    }

    /// <summary>Polls a condition on the worker's real-time loops.</summary>
    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("timed out waiting for condition");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await Worker.StopAsync(giveUp.Token); } catch (OperationCanceledException) { }
        Worker.Dispose();
        await _port.DisposeAsync();
        _cts.Dispose();
    }
}
