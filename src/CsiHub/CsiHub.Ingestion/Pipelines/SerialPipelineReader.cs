using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// One serial-port pipeline reader and writer. Wraps an <see cref="ISerialPort.BaseStream"/> with
/// <see cref="PipeReader"/> to parse NDJSON lines in a non-blocking, low-allocation manner,
/// while a concurrent write loop sends host-to-node NDJSON commands. Disconnects are
/// published as state events and trigger a graceful reconnect.
/// </summary>
public sealed class SerialPipelineReader
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes("\n");

    private readonly string _portName;
    private readonly Func<ISerialPort> _portFactory;
    private readonly int _reconnectDelayMs;
    private readonly CsiIngestionChannel _channel;
    private readonly ILogger _logger;
    private readonly Channel<string> _commandChannel;

    private string? _lastMac;
    private NodeConnectionState _lastState = NodeConnectionState.Disconnected;

    public SerialPipelineReader(
        string portName,
        Func<ISerialPort> portFactory,
        int commandChannelCapacity,
        int reconnectDelayMs,
        CsiIngestionChannel channel,
        ILogger logger)
    {
        _portName = portName;
        _portFactory = portFactory;
        _reconnectDelayMs = reconnectDelayMs;
        _channel = channel;
        _logger = logger;

        _commandChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(commandChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Queues a host-to-node NDJSON command for the serial write loop.
    /// </summary>
    public bool TrySendCommand(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        return _commandChannel.Writer.TryWrite(json);
    }

    /// <summary>
    /// Runs the read/reconnect loop until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);

                // RunOnceAsync returned normally (stream ended). Publish disconnected
                // and attempt a reconnect after the configured delay.
                PublishState(NodeConnectionState.Disconnected);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishState(NodeConnectionState.Disconnected);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Serial reader for {Port} encountered an error; reconnecting in {Delay}ms.",
                    _portName,
                    _reconnectDelayMs);

                PublishState(NodeConnectionState.Disconnected);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_reconnectDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var port = _portFactory();

        port.Open();

        _logger.LogInformation(
            "Opened serial port {Port}.",
            _portName);

        // The first heartbeat or state-bearing payload will publish the real state
        // and carry the node's MAC and uptime, so we do not emit an empty Standby here.
        var reader = PipeReader.Create(
            port.BaseStream,
            new StreamPipeReaderOptions(
                pool: MemoryPool<byte>.Shared,
                bufferSize: 4096,
                minimumReadSize: 1,
                leaveOpen: true));

        // On Linux (and some Windows drivers) SerialStream.ReadAsync does not honor the
        // CancellationToken while it is blocked waiting for data. Register a callback that
        // closes the port so the blocked read returns immediately.
        CancellationTokenRegistration cancelRegistration = default;

        try
        {
            cancelRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (port.IsOpen)
                    {
                        port.Close();
                    }
                }
                catch
                {
                    // Swallow; the goal is simply to unblock ReadAsync.
                }
            });

            Task writeLoop = RunWriteLoopAsync(port, cancellationToken);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ReadResult result;

                    try
                    {
                        result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException("Serial read canceled by port close.", ex, cancellationToken);
                        }

                        throw;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException("Serial read canceled by port close.", ex, cancellationToken);
                        }

                        throw;
                    }

                    ReadOnlySequence<byte> buffer = result.Buffer;

                    while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                    {
                        ProcessLine(line);
                    }

                    // Advance the consumed pointer to the end of the parsed lines and the
                    // examined pointer to the end of the buffer, preserving any partial
                    // NDJSON frame across the next ReadAsync call.
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    await writeLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the write loop is shut down.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Serial write loop for {Port} failed.", _portName);
                }

                reader.Complete();

                if (port.IsOpen)
                {
                    port.Close();
                }
            }
        }
        finally
        {
            cancelRegistration.Dispose();
        }
    }

    private async Task RunWriteLoopAsync(ISerialPort port, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string json in _commandChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                byte[] command = Encoding.UTF8.GetBytes(json);

                try
                {
                    _logger.LogDebug("Serial write to {Port}: {Command}", _portName, json);

                    await port.BaseStream.WriteAsync(command, 0, command.Length, cancellationToken).ConfigureAwait(false);
                    await port.BaseStream.WriteAsync(NewLineBytes, 0, NewLineBytes.Length, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Port was closed to unblock the reader during shutdown.
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Serial write to {Port} failed; port may have been closed.", _portName);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        line = default;

        SequencePosition? position = buffer.PositionOf((byte)'\n');
        if (position is null)
        {
            return false;
        }

        // Slice the line, excluding the newline terminator.
        line = buffer.Slice(0, position.Value);

        // Trim a trailing '\r' if the line was terminated with '\r\n'.
        if (line.Length > 0)
        {
            var lastPosition = line.GetPosition(line.Length - 1);
            if (line.Slice(lastPosition, 1).First.Span[0] == (byte)'\r')
            {
                line = line.Slice(0, line.Length - 1);
            }
        }

        // Advance the unconsumed buffer to the byte after the newline.
        var next = buffer.GetPosition(1, position.Value);
        buffer = buffer.Slice(next);

        return true;
    }

    private static bool TryFindFirstNonWhitespace(ReadOnlySequence<byte> line, out byte value)
    {
        value = 0;

        foreach (var segment in line)
        {
            ReadOnlySpan<byte> span = segment.Span;
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                {
                    value = b;
                    return true;
                }
            }
        }

        return false;
    }

    private void ProcessLine(ReadOnlySequence<byte> line)
    {
        if (line.Length == 0)
        {
            return;
        }

        if (!TryFindFirstNonWhitespace(line, out byte first) || first != (byte)'{')
        {
            int previewLength = (int)Math.Min(line.Length, 200);
            var preview = line.Slice(0, previewLength).ToArray();
            var lineText = Encoding.UTF8.GetString(preview);

            _logger.LogWarning(
                "Ignoring non-JSON-object NDJSON line from {Port}: {Line}",
                _portName,
                lineText);
            return;
        }

        try
        {
            if (line.IsSingleSegment)
            {
                ProcessSpan(line.First.Span);
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent((int)line.Length);
                try
                {
                    line.CopyTo(rented);
                    ProcessSpan(new ReadOnlySpan<byte>(rented, 0, (int)line.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }
            }
        }
        catch (JsonException ex)
        {
            long position = ex.BytePositionInLine ?? 0;
            int windowStart = (int)Math.Max(0, position - 100);
            int windowLength = (int)Math.Min(line.Length - windowStart, 200);
            if (windowLength <= 0)
            {
                windowStart = 0;
                windowLength = (int)Math.Min(line.Length, 200);
            }

            var window = line.Slice(windowStart, windowLength).ToArray();
            var windowText = Encoding.UTF8.GetString(window);

            _logger.LogWarning(ex,
                "Failed to parse NDJSON line from {Port} at position {Position} (length {Length}): {Window}",
                _portName,
                position,
                line.Length,
                windowText);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process NDJSON line from {Port}.", _portName);
        }
    }

    private void ProcessSpan(ReadOnlySpan<byte> json)
    {
        var payload = JsonSerializer.Deserialize<NodePayload>(json, JsonOptions);
        if (payload is null)
        {
            return;
        }

        payload.ReceivedAt = DateTimeOffset.UtcNow;
        payload.PortName = _portName;

        if (!string.IsNullOrEmpty(payload.Mac))
        {
            _lastMac = payload.Mac;
        }

        _channel.TryPublish(payload);

        if (!string.IsNullOrEmpty(payload.State))
        {
            bool isHeartbeat = string.Equals(payload.Type, "hb", StringComparison.Ordinal);
            PublishState(
                ParseConnectionState(payload.State),
                payload.Timestamp,
                payload.ReceivedAt,
                force: isHeartbeat,
                clockLeader: payload.ClockLeader,
                imuHost: payload.ImuHost,
                bandwidth: payload.Bandwidth);
        }
    }

    private static NodeConnectionState ParseConnectionState(string? state)
    {
        var lowered = state?.ToLowerInvariant();

        return lowered switch
        {
            "standby" or "boot" => NodeConnectionState.Standby,
            "streaming" => NodeConnectionState.Streaming,
            null => NodeConnectionState.Disconnected,
            _ when lowered.StartsWith("diag_") => NodeConnectionState.Assigned,
            _ => NodeConnectionState.Standby
        };
    }

    private void PublishState(
        NodeConnectionState state,
        long? uptime = null,
        DateTimeOffset? receivedAt = null,
        bool force = false,
        bool? clockLeader = null,
        bool? imuHost = null,
        int? bandwidth = null)
    {
        if (!force && _lastState == state)
        {
            return;
        }

        _lastState = state;

        var change = new NodeStateChanged(
            _portName,
            _lastMac,
            state,
            DateTimeOffset.UtcNow,
            uptime,
            receivedAt,
            clockLeader,
            imuHost,
            bandwidth);

        _channel.TryPublishState(change);
    }
}
