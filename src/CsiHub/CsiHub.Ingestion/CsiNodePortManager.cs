using System.Collections.Concurrent;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion;

/// <summary>
/// Singleton manager that owns one <see cref="SerialPipelineReader"/> per configured
/// COM port. Started by <see cref="CsiIngestionBackgroundService"/> and runs each
/// reader as a long-running, fault-tolerant background task.
/// </summary>
public sealed class CsiNodePortManager
{
    private readonly CsiIngestionOptions _options;
    private readonly CsiIngestionChannel _channel;
    private readonly ISerialPortFactory _portFactory;
    private readonly ILogger<CsiNodePortManager> _logger;

    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, SerialPipelineReader> _readers = new();
    private readonly List<Task> _tasks = new();

    public CsiNodePortManager(
        IOptions<CsiIngestionOptions> options,
        CsiIngestionChannel channel,
        ISerialPortFactory portFactory,
        ILogger<CsiNodePortManager> logger)
    {
        _options = options.Value;
        _channel = channel;
        _portFactory = portFactory;
        _logger = logger;
    }

    /// <summary>
    /// Opens the configured serial ports and starts the long-running read/reconnect loop for each.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("CsiNodePortManager has already been started.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readers.Clear();
        _tasks.Clear();

        foreach (string portName in _options.SerialPortNames)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                continue;
            }

            var reader = new SerialPipelineReader(
                portName,
                () => _portFactory.Create(portName, _options.SerialBaudRate),
                _options.CommandChannelCapacity,
                _options.ReconnectDelayMs,
                _channel,
                _logger);

            _readers[portName] = reader;

            Task task = Task.Factory.StartNew(
                () => reader.RunAsync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();

            _tasks.Add(task);
        }

        _logger.LogInformation(
            "Started {Count} serial ingestion reader(s).",
            _tasks.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues an NDJSON command for the specified port. Returns false if the port is not open.
    /// </summary>
    public bool TrySendCommand(string portName, string json)
    {
        if (_readers.TryGetValue(portName, out var reader))
        {
            return reader.TrySendCommand(json);
        }

        _logger.LogWarning("Cannot send command to {Port}; port is not configured.", portName);
        return false;
    }

    /// <summary>
    /// Sends a command to the specified port and waits for a matching ACK,
    /// retrying up to <see cref="CsiIngestionOptions.CommandRetries"/> times.
    /// </summary>
    public async Task<Ack?> SendCommandAsync(
        string portName,
        string json,
        CancellationToken cancellationToken = default)
    {
        if (_readers.TryGetValue(portName, out var reader))
        {
            return await reader.SendCommandAsync(
                json,
                _options.CommandTimeoutMs,
                _options.CommandRetries,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Cannot send command to {Port}; port is not configured.", portName);
        return null;
    }

    /// <summary>
    /// Cancels all readers and waits up to five seconds for them to stop.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _cts?.Cancel();

            if (_tasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(_tasks)
                        .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timed out waiting for serial readers to stop.");
                }
                catch (OperationCanceledException)
                {
                    // Host requested an immediate shutdown.
                }
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}
