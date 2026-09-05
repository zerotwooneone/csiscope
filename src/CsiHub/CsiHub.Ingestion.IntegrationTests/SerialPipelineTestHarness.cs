using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.IntegrationTests.Fakes;
using CsiHub.Ingestion.Pipelines;
using CsiHub.Ingestion.Pipelines.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion.IntegrationTests;

/// <summary>
/// Self-cleaning harness for <see cref="SerialPipelineReader"/> integration tests.
/// Implements <see cref="IAsyncDisposable"/> so a failed assertion still cancels the
/// background loop and tears down the loopback socket.
/// </summary>
public sealed class SerialPipelineTestHarness : IAsyncDisposable
{
    private readonly FakeSerialPort _port;
    private readonly CsiIngestionChannel _channel;
    private readonly SerialPipelineReader _reader;
    private readonly CancellationTokenSource _cts;
    private readonly Task _runTask;
    private int _disposed;

    public SerialPipelineTestHarness(string portName)
    {
        _port = new FakeSerialPort(portName);
        _channel = new CsiIngestionChannel(Options.Create(new CsiIngestionOptions
        {
            PayloadChannelCapacity = 16,
            StateChannelCapacity = 16,
            CommandChannelCapacity = 16
        }));

        _reader = new SerialPipelineReader(
            portName,
            () => _port,
            commandChannelCapacity: 16,
            reconnectDelayMs: 2000,
            _channel,
            NullLogger<SerialPipelineReader>.Instance,
            new IPayloadHandler[]
            {
                new ConfigHandler(),
                new HeartbeatHandler(),
                new AckHandler(),
                new TelemetryHandler(),
                new IgnoredHandler(),
            });

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => _reader.RunAsync(_cts.Token));
    }

    public FakeSerialPort Port => _port;

    public CsiIngestionChannel Channel => _channel;

    public SerialPipelineReader Reader => _reader;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Best-effort; the port close below will unblock the read loop.
            }
            catch (OperationCanceledException)
            {
                // Expected when the harness token is cancelled.
            }
        }
        finally
        {
            await _port.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
