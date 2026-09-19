using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using CsiScope.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiScope.Infrastructure;

/// <summary>
/// Owns the serial lifecycle and enforces the orchestrator's single-writer
/// contract: each port gets a pump task that frames and parses NDJSON lines
/// into a bounded ingress channel, while ONE consumer loop performs every
/// <see cref="SensingOrchestrator"/> mutation (ingest, roster, tick) on a
/// single thread — lock-free by construction.
///
/// Each open port also gets a <see cref="NodeSerialAdapter"/> registered with
/// the <see cref="BroadcastRadioAdapter"/> composite for the lifetime of the
/// connection, so commands and ACKs stay per-port.
///
/// Tick starvation is impossible: the consumer waits on the ingress channel
/// with a timeout equal to the tick interval, so a silent bus still ticks.
/// </summary>
public sealed class SensingHostWorker : BackgroundService
{
    private const int IngressCapacity = 1000;
    private const int MaxLineBytes = 4096; // firmware CsiJsonBufferSize ceiling

    private readonly SensingOrchestrator _orchestrator;
    private readonly BroadcastRadioAdapter _radio;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _portNames;
    private readonly Func<string, CancellationToken, ValueTask<Stream>> _openStream;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _reconnectDelay;
    private readonly ILogger<SensingHostWorker>? _logger;
    private readonly Channel<IngressItem> _ingress;

    public SensingHostWorker(
        SensingOrchestrator orchestrator,
        BroadcastRadioAdapter radio,
        TimeProvider time,
        IReadOnlyList<string> portNames,
        Func<string, CancellationToken, ValueTask<Stream>> openStream,
        TimeSpan? tickInterval = null,
        TimeSpan? reconnectDelay = null,
        ILogger<SensingHostWorker>? logger = null)
    {
        _orchestrator = orchestrator;
        _radio = radio;
        _time = time;
        _portNames = portNames;
        _openStream = openStream;
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(100);
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
        _logger = logger;
        _ingress = Channel.CreateBounded<IngressItem>(new BoundedChannelOptions(IngressCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pumps = _portNames.Select(p => PumpPortAsync(p, stoppingToken)).ToList();
        pumps.Add(ConsumeLoopAsync(stoppingToken));
        await Task.WhenAll(pumps);
    }

    // ---- Single-writer consumer: the ONLY thread touching the orchestrator ----

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(_tickInterval);
            try
            {
                while (await _ingress.Reader.WaitToReadAsync(window.Token))
                {
                    while (_ingress.Reader.TryRead(out var item))
                    {
                        Dispatch(in item);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // window elapsed — fall through to tick
            }

            _orchestrator.Tick(_time.GetUtcNow());
        }
    }

    private void Dispatch(in IngressItem item)
    {
        var now = _time.GetUtcNow();
        switch (item.Frame.Kind)
        {
            case TelemetryKind.Csi:
                var sample = item.Frame.Sample;
                _orchestrator.OnAmplitudeSampleReceived(in sample);
                break;
            case TelemetryKind.Ack:
                _radio.NotifyAck(item.Port, item.Frame.Ack.Seq, item.Frame.Ack.Success);
                break;
            case TelemetryKind.Config:
                _orchestrator.RegisterExpectedNode(item.Frame.AnnouncedMac, now);
                break;
            case TelemetryKind.Heartbeat:
                _orchestrator.OnNodeHeartbeat(item.Frame.AnnouncedMac, now);
                break;
        }
    }

    // ---- Per-port pump: frame + parse only, never touches the orchestrator ----

    private async Task PumpPortAsync(string portName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            NodeSerialAdapter? nodeAdapter = null;
            try
            {
                stream = await _openStream(portName, ct);

                // Egress for this port lives exactly as long as the connection.
                var s = stream;
                nodeAdapter = new NodeSerialAdapter((bytes, token) => s.WriteAsync(bytes, token));
                _radio.RegisterNode(portName, nodeAdapter);

                await PumpStreamAsync(stream, portName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Serial port {Port} dropped; reconnecting", portName);
            }
            finally
            {
                _radio.UnregisterNode(portName);
                if (nodeAdapter is not null)
                {
                    await nodeAdapter.DisposeAsync();
                }

                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }
            }

            try
            {
                await Task.Delay(_reconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PumpStreamAsync(Stream stream, string portName, CancellationToken ct)
    {
        var reader = PipeReader.Create(stream);
        // Reusable line buffer — multi-segment lines copy here instead of
        // stackalloc-ing per line (firmware caps lines at 4 KiB).
        var lineBuffer = new byte[MaxLineBytes];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = ProcessBuffer(ref buffer, lineBuffer, portName);
                reader.AdvanceTo(consumed, buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private SequencePosition ProcessBuffer(ref ReadOnlySequence<byte> buffer, byte[] lineBuffer, string portName)
    {
        var reader = new SequenceReader<byte>(buffer);
        while (reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
        {
            if (line.Length is 0 or > MaxLineBytes)
            {
                continue;
            }

            // Strip a trailing \r so CRLF firmware also parses.
            if (line.Slice(line.Length - 1, 1).FirstSpan[0] == (byte)'\r')
            {
                line = line.Slice(0, line.Length - 1);
            }

            if (line.IsSingleSegment)
            {
                Enqueue(line.FirstSpan, portName);
            }
            else
            {
                line.CopyTo(lineBuffer);
                Enqueue(lineBuffer.AsSpan(0, (int)line.Length), portName);
            }
        }

        var consumed = reader.Position;
        buffer = buffer.Slice(consumed);
        return consumed;
    }

    private void Enqueue(ReadOnlySpan<byte> line, string portName)
    {
        if (TelemetryParser.TryParse(line, _time.GetUtcNow(), out var frame))
        {
            _ingress.Writer.TryWrite(new IngressItem(portName, frame));
        }
    }

    private readonly record struct IngressItem(string Port, ParsedTelemetry Frame);
}
