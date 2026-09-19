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
/// Tick starvation is impossible: the consumer waits on the ingress channel
/// with a timeout equal to the tick interval, so a silent bus still ticks.
/// </summary>
public sealed class SensingHostWorker : BackgroundService
{
    private const int IngressCapacity = 1000;
    private const int MaxLineBytes = 4096;

    private readonly SensingOrchestrator _orchestrator;
    private readonly SerialRadioAdapter _radio;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _portNames;
    private readonly Func<string, CancellationToken, ValueTask<Stream>> _openStream;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _reconnectDelay;
    private readonly ILogger<SensingHostWorker>? _logger;
    private readonly Channel<ParsedTelemetry> _ingress;

    public SensingHostWorker(
        SensingOrchestrator orchestrator,
        SerialRadioAdapter radio,
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
        _ingress = Channel.CreateBounded<ParsedTelemetry>(new BoundedChannelOptions(IngressCapacity)
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
                    while (_ingress.Reader.TryRead(out var frame))
                    {
                        Dispatch(frame);
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

    private void Dispatch(in ParsedTelemetry frame)
    {
        switch (frame.Kind)
        {
            case TelemetryKind.Csi:
                var sample = frame.Sample;
                _orchestrator.OnAmplitudeSampleReceived(in sample);
                break;
            case TelemetryKind.Ack:
                _radio.NotifyAck(frame.Ack.Seq, frame.Ack.Success);
                break;
            case TelemetryKind.Config:
                _orchestrator.RegisterExpectedNode(frame.AnnouncedMac);
                break;
        }
    }

    // ---- Per-port pump: frame + parse only, never touches the orchestrator ----

    private async Task PumpPortAsync(string portName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            try
            {
                stream = await _openStream(portName, ct);
                await PumpStreamAsync(stream, ct);
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

    private async Task PumpStreamAsync(Stream stream, CancellationToken ct)
    {
        var reader = PipeReader.Create(stream);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = ProcessBuffer(ref buffer);
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

    private SequencePosition ProcessBuffer(ref ReadOnlySequence<byte> buffer)
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
                Enqueue(line.FirstSpan);
            }
            else
            {
                Span<byte> span = stackalloc byte[(int)line.Length];
                line.CopyTo(span);
                Enqueue(span);
            }
        }

        var consumed = reader.Position;
        buffer = buffer.Slice(consumed);
        return consumed;
    }

    private void Enqueue(ReadOnlySpan<byte> line)
    {
        if (TelemetryParser.TryParse(line, _time.GetUtcNow(), out var frame))
        {
            _ingress.Writer.TryWrite(frame);
        }
    }
}
