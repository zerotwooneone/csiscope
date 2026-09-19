using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using CsiScope.Application.Ports;
using CsiScope.Domain.Model;

namespace CsiScope.Infrastructure;

/// <summary>
/// <see cref="IRadioCommandPort"/> over the serial wire. Commands are queued
/// (bounded, drop-oldest — a stale command is worse than none) and written by
/// a single send loop, so wire writes are always serialized. Each command
/// carries an incrementing <c>seq</c>; the loop awaits the matching
/// <c>{"type":"ack"}</c> frame with a timeout and one retry.
///
/// Firmware contract: passive mode REQUIRES a non-empty mac_filter. An empty
/// filter therefore emits the single-channel-scan variant
/// (<c>dwell_ms</c>) — the host controls actual dwell time by re-commanding
/// on each hop.
/// </summary>
public sealed class SerialRadioAdapter : IRadioCommandPort, IAsyncDisposable
{
    private const int QueueCapacity = 32;
    private const int ScanDwellMs = 5000; // firmware max; host re-commands sooner

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _broadcastAsync;
    private readonly Channel<OutboundCommand> _queue;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sendLoop;
    private readonly TimeSpan _ackTimeout;
    private readonly int _maxAttempts;
    private long _seq;

    /// <param name="broadcastAsync">Fans the framed bytes out to every open node stream.</param>
    public SerialRadioAdapter(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> broadcastAsync,
        TimeSpan? ackTimeout = null,
        int maxAttempts = 2)
    {
        _broadcastAsync = broadcastAsync;
        _ackTimeout = ackTimeout ?? TimeSpan.FromMilliseconds(1000);
        _maxAttempts = Math.Max(1, maxAttempts);
        _queue = Channel.CreateBounded<OutboundCommand>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _sendLoop = Task.Run(SendLoopAsync);
    }

    /// <summary>Queues the command; completes when the firmware ACKs or all attempts time out.</summary>
    public Task<bool> BroadcastSetRfAsync(WifiChannel channel, ImmutableArray<MacAddress> macFilter, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new OutboundCommand(channel, macFilter, completion)))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    /// <summary>Called by the host worker when an ack frame arrives on any node stream.</summary>
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
                var seq = Interlocked.Increment(ref _seq);
                var payload = BuildSetRfFrame(command.Channel, command.MacFilter, seq);
                var acked = false;

                for (var attempt = 0; attempt < _maxAttempts && !acked; attempt++)
                {
                    var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingAcks[seq] = wait;
                    try
                    {
                        await _broadcastAsync(payload, _stop.Token);
                        acked = await wait.Task.WaitAsync(_ackTimeout, _stop.Token);
                    }
                    catch (TimeoutException)
                    {
                        // retry with the same seq — firmware dedupes on receipt
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    finally
                    {
                        _pendingAcks.TryRemove(seq, out _);
                    }
                }

                command.Completion.TrySetResult(acked);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
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
