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
            portNames: new[] { "LOOPBACK1" },
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

    /// <summary>Starts the worker's pumps + consumer loop.</summary>
    public Task StartAsync() => Worker.StartAsync(_cts.Token);

    /// <summary>Writes one NDJSON line as the firmware node would.</summary>
    public async Task WriteLineAsync(string json)
        => await WriteRawAsync(json + "\n");

    /// <summary>Writes raw bytes — for partial/torn frame tests.</summary>
    public async Task WriteRawAsync(string text)
    {
        await Node.WriteAsync(Encoding.UTF8.GetBytes(text));
        await Node.FlushAsync();
    }

    /// <summary>Reads one newline-terminated frame the host wrote to the node.</summary>
    public async Task<string> ReadLineAsync(TimeSpan timeout)
    {
        var buffer = new byte[4096];
        var line = new List<byte>();
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var read = await Node.ReadAsync(buffer, cts.Token);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    return Encoding.UTF8.GetString(line.ToArray());
                }

                line.Add(buffer[i]);
            }
        }
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
