using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
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

    private readonly string _portName;
    private readonly Func<ISerialPort> _portFactory;
    private readonly int _reconnectDelayMs;
    private readonly CsiIngestionChannel _channel;
    private readonly ILogger _logger;
    private readonly Channel<string> _commandChannel;
    private readonly PayloadDispatcher _dispatcher;
    private readonly SerialPipelineContext _context;

    private int _nextSeq = 0;

    public SerialPipelineReader(
        string portName,
        Func<ISerialPort> portFactory,
        int commandChannelCapacity,
        int reconnectDelayMs,
        CsiIngestionChannel channel,
        ILogger logger,
        IEnumerable<IPayloadHandler> handlers)
    {
        _portName = portName;
        _portFactory = portFactory;
        _reconnectDelayMs = reconnectDelayMs;
        _channel = channel;
        _logger = logger;
        _dispatcher = new PayloadDispatcher(handlers);
        _context = new SerialPipelineContext(portName, channel, logger);

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
    /// Sends a command and waits for an ACK with matching sequence number.
    /// The command JSON is decorated with a "seq" field before transmission.
    /// </summary>
    public async Task<Ack?> SendCommandAsync(string commandJson, int timeoutMs, int retries, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandJson))
        {
            throw new ArgumentException("Command JSON cannot be empty.", nameof(commandJson));
        }

        for (int attempt = 0; attempt < retries; attempt++)
        {
            int seq = Interlocked.Increment(ref _nextSeq);
            string? framedJson = AddSeqToJson(commandJson, seq);
            if (framedJson is null)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<Ack>(TaskCreationOptions.RunContinuationsAsynchronously);
            _context.PendingAcks[seq] = tcs;

            try
            {
                if (!TrySendCommand(framedJson))
                {
                    _context.PendingAcks.TryRemove(seq, out _);
                    _logger.LogWarning("Command channel full; dropping command to {Port}.", _portName);
                    continue;
                }

                await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _context.PendingAcks.TryRemove(seq, out _);

                if (attempt == retries - 1)
                {
                    _logger.LogWarning(
                        "Command to {Port} timed out after {Retries} attempts (timeout {Timeout}ms).",
                        _portName,
                        retries,
                        timeoutMs);
                    throw;
                }

                _logger.LogDebug(
                    "Command to {Port} timed out (attempt {Attempt}/{Retries}); retrying.",
                    _portName,
                    attempt + 1,
                    retries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _context.PendingAcks.TryRemove(seq, out _);
                throw;
            }
            catch (Exception ex)
            {
                _context.PendingAcks.TryRemove(seq, out _);
                _logger.LogWarning(ex, "Command to {Port} failed.", _portName);
                return null;
            }
        }

        return null;
    }

    private static string? AddSeqToJson(string commandJson, int seq)
    {
        try
        {
            using var doc = JsonDocument.Parse(commandJson);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                writer.WriteNumber("seq", seq);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return null;
        }
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
                _context.PublishState(NodeConnectionState.Disconnected);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _context.PublishState(NodeConnectionState.Disconnected);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Serial reader for {Port} encountered an error; reconnecting in {Delay}ms.",
                    _portName,
                    _reconnectDelayMs);

                _context.PublishState(NodeConnectionState.Disconnected);
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

        // Wait for the node to announce its config before processing telemetry.
        _context.HasSeenConfig = false;

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

                    while (SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> frame))
                    {
                        ProcessFrame(frame);
                    }

                    // Advance the consumed pointer to the start of the next unprocessed
                    // frame and the examined pointer to the end of the buffer, preserving
                    // any partial frame across the next ReadAsync call.
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

                byte[] frame = SerialFraming.CreateFrame(json);

                try
                {
                    _logger.LogDebug("Serial write to {Port}: {Command}", _portName, json);

                    await port.BaseStream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
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

    private void ProcessFrame(ReadOnlySequence<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        byte[]? rented = null;
        ReadOnlyMemory<byte> memory;

        try
        {
            if (payload.IsSingleSegment)
            {
                memory = payload.First;
            }
            else
            {
                rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                payload.CopyTo(rented);
                memory = new ReadOnlyMemory<byte>(rented, 0, (int)payload.Length);
            }

            ReadOnlySpan<byte> span = memory.Span;
            var nodePayload = JsonSerializer.Deserialize<NodePayload>(span, JsonOptions);
            if (nodePayload is null || string.IsNullOrWhiteSpace(nodePayload.Type))
            {
                return;
            }

            nodePayload.ReceivedAt = DateTimeOffset.UtcNow;
            nodePayload.PortName = _portName;

            if (!string.IsNullOrEmpty(nodePayload.Mac))
            {
                _context.LastMac = nodePayload.Mac;
            }

            if (!_context.HasSeenConfig)
            {
                if (nodePayload.Type == "config")
                {
                    _dispatcher.Dispatch(nodePayload, span, _context);
                }
                else
                {
                    _logger.LogDebug("Discarding pre-config frame from {Port}.", _portName);
                }

                return;
            }

            _dispatcher.Dispatch(nodePayload, span, _context);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON frame from {Port}.", _portName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process JSON frame from {Port}.", _portName);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }
}
