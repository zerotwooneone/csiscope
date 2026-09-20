using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using CsiScope.Domain.Model;

namespace CsiScope.Infrastructure;

/// <summary>
/// Command egress for ONE physical node serial port. Owns its seq space and
/// pending-ACK table, so ACKs from different ports can never cross-complete.
///
/// Queue safety: bounded channel (32) with MANUAL eviction — a dropped
/// command's caller is always completed false, never orphaned. NACKs
/// (success:false) fail fast — the firmware rejected the command, retrying
/// is pointless. The write loop survives transport failures: a dead port
/// fails the current command but the loop stays alive for reconnects.
///
/// Firmware contract: passive mode REQUIRES a non-empty mac_filter, so an
/// empty filter emits the single-channel-scan variant (dwell_ms) — the host
/// controls actual dwell by re-commanding on each hop.
/// </summary>
public sealed class NodeSerialAdapter : IAsyncDisposable
{
    private const int QueueCapacity = 32;
    // Must stay below the host's per-channel hop interval (SurveyDwell = 500ms):
    // each set_rf re-command calls resetMetrics() which restarts the dwell clock,
    // so a dwell longer than the hop interval never elapses and emitMetrics()
    // (rf_scan) never fires. 250ms completes + emits with margin before the next hop.
    private const int ScanDwellMs = 250;

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _writeAsync;
    private readonly TimeProvider _time;
    private readonly Channel<OutboundCommand> _queue;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sendLoop;
    private readonly TimeSpan _ackTimeout;
    private readonly int _maxAttempts;
    private long _seq;

    /// <param name="writeAsync">Writes one framed NDJSON line to this node's stream.</param>
    /// <param name="time">Clock for ACK timeouts — injectable for deterministic tests.</param>
    public NodeSerialAdapter(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeAsync,
        TimeProvider? time = null,
        TimeSpan? ackTimeout = null,
        int maxAttempts = 2)
    {
        _writeAsync = writeAsync;
        _time = time ?? TimeProvider.System;
        _ackTimeout = ackTimeout ?? TimeSpan.FromMilliseconds(1000);
        _maxAttempts = Math.Max(1, maxAttempts);
        _queue = Channel.CreateBounded<OutboundCommand>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _sendLoop = Task.Run(SendLoopAsync);
    }

    /// <summary>Queues the command; completes true on ACK, false on NACK/timeout/eviction.</summary>
    public Task<bool> SendSetRfAsync(WifiChannel channel, ImmutableArray<MacAddress> macFilter, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new OutboundCommand(channel, macFilter, completion);

        // Manual drop-oldest: evicted commands are completed false so callers
        // never hang on an orphaned task.
        while (!_queue.Writer.TryWrite(command))
        {
            if (_queue.Reader.TryRead(out var dropped))
            {
                dropped.Completion.TrySetResult(false);
                continue;
            }

            if (_queue.Reader.Completion.IsCompleted)
            {
                completion.TrySetResult(false);
                return completion.Task;
            }
            // full->empty race with the reader: retry TryWrite
        }

        return completion.Task;
    }

    /// <summary>Called by the host worker when an ack frame arrives on THIS port.</summary>
    public void NotifyAck(long seq, bool success)
    {
        if (_pendingAcks.TryRemove(seq, out var tcs))
        {
            tcs.TrySetResult(success);
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var command in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    await ExecuteAsync(command);
                }
                catch (Exception)
                {
                    // One bad command must never kill the loop.
                    command.Completion.TrySetResult(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task ExecuteAsync(OutboundCommand command)
    {
        var seq = Interlocked.Increment(ref _seq);
        var frame = SerialFrameCodec.EncodeFrame(BuildSetRfFrame(command.Channel, command.MacFilter, seq));
        bool? result = null;

        for (var attempt = 0; attempt < _maxAttempts && result is null; attempt++)
        {
            var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[seq] = wait;
            try
            {
                await _writeAsync(frame, _stop.Token);
                // Completes true on ACK, false on NACK — both terminal.
                result = await wait.Task.WaitAsync(_ackTimeout, _time, _stop.Token);
            }
            catch (TimeoutException)
            {
                // no ack — retry with the same seq
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // transport failure — fail the command, keep the loop alive
                result = false;
            }
            finally
            {
                _pendingAcks.TryRemove(seq, out _);
            }
        }

        command.Completion.TrySetResult(result ?? false);
    }

    private static byte[] BuildSetRfFrame(WifiChannel channel, ImmutableArray<MacAddress> macFilter, long seq)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("cmd"u8, "set_rf"u8);
            writer.WriteNumber("ch"u8, (int)channel);
            writer.WriteNumber("seq"u8, seq);
            if (macFilter.IsEmpty)
            {
                // Passive mode rejects an empty filter — emit the scan variant.
                writer.WriteNumber("dwell_ms"u8, ScanDwellMs);
            }
            else
            {
                writer.WriteNumber("bw"u8, 20);
                writer.WriteString("mode"u8, "passive"u8);
                writer.WriteStartArray("mac_filter"u8);
                foreach (var mac in macFilter)
                {
                    writer.WriteStringValue(mac.ToCanonicalString());
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        var length = buffer.WrittenCount;
        var framed = new byte[length + 1];
        buffer.WrittenSpan.CopyTo(framed);
        framed[length] = (byte)'\n';
        return framed;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync();
        try
        {
            await _sendLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private readonly record struct OutboundCommand(
        WifiChannel Channel,
        ImmutableArray<MacAddress> MacFilter,
        TaskCompletionSource<bool> Completion);
}
