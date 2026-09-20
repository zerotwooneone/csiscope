using System.Collections.Concurrent;
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

    // Snapshot cadence matches the Blazor polling rate — faster is wasted GC.
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(333);

    private readonly SensingOrchestrator _orchestrator;
    private readonly BroadcastRadioAdapter _radio;
    private readonly TimeProvider _time;
    private readonly Func<string, CancellationToken, ValueTask<Stream>> _openStream;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _ackTimeout;
    private readonly int _maxAttempts;
    private readonly ILogger<SensingHostWorker>? _logger;
    private readonly Channel<IngressItem> _ingress;
    private readonly ConcurrentDictionary<string, byte> _portsSeen = new();
    private readonly SemaphoreSlim _activation = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _sessionCts;
    private IReadOnlyList<string> _activePorts = Array.Empty<string>();
    private volatile bool _isSensing;
    private SensingSnapshot? _latestSnapshot;
    private DateTimeOffset _lastSnapshotAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Most recent orchestrator projection — atomically swapped each snapshot
    /// interval, safe for UI threads to poll without locks.
    /// </summary>
    public SensingSnapshot? LatestSnapshot => Volatile.Read(ref _latestSnapshot);

    public SensingHostWorker(
        SensingOrchestrator orchestrator,
        BroadcastRadioAdapter radio,
        TimeProvider time,
        Func<string, CancellationToken, ValueTask<Stream>> openStream,
        TimeSpan? tickInterval = null,
        TimeSpan? reconnectDelay = null,
        ILogger<SensingHostWorker>? logger = null,
        TimeSpan? ackTimeout = null,
        int maxAttempts = 2)
    {
        _orchestrator = orchestrator;
        _radio = radio;
        _time = time;
        _openStream = openStream;
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(100);
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
        _ackTimeout = ackTimeout ?? TimeSpan.FromMilliseconds(1000);
        _maxAttempts = maxAttempts;
        _logger = logger;
        _ingress = Channel.CreateBounded<IngressItem>(new BoundedChannelOptions(IngressCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    /// <summary>True while a sensing session is active (ports pumping).</summary>
    public bool IsSensing => _isSensing;

    /// <summary>Ports assigned to the current/last session, in array-position order.</summary>
    public IReadOnlyList<string> ActivePorts => _activePorts;

    /// <summary>
    /// Begin pumping the given ports. Restarts cleanly if a session is already
    /// running — existing pumps are cancelled and respawned with the new set.
    /// </summary>
    public void StartSensing(IReadOnlyList<string> ports)
    {
        lock (_gate)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
            _activePorts = ports.ToArray();
            _isSensing = ports.Count > 0;
        }

        // Drain any stale release, then signal the supervisor once.
        while (_activation.Wait(0)) { }
        _activation.Release();
    }

    /// <summary>Stop all port pumps; the consumer keeps ticking so the UI still gets snapshots.</summary>
    public void StopSensing()
    {
        lock (_gate)
        {
            _isSensing = false;
            _activePorts = Array.Empty<string>();
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ConsumeLoopAsync(stoppingToken), SupervisorAsync(stoppingToken));
    }

    // ---- Session supervisor: idles until StartSensing, owns pump lifetime ----

    private async Task SupervisorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _activation.WaitAsync(ct);

            IReadOnlyList<string> ports;
            CancellationTokenSource session;
            lock (_gate)
            {
                ports = _activePorts;
                session = new CancellationTokenSource();
                _sessionCts = session;
            }

            _portsSeen.Clear();
            _logger?.LogInformation("Sensing session starting on {Ports}", string.Join(", ", ports));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
            var pumps = ports.Select(p => PumpPortAsync(p, linked.Token)).ToList();
            try
            {
                await Task.WhenAll(pumps);
            }
            catch (OperationCanceledException)
            {
                // session or host cancelled — loop back and idle
            }
            finally
            {
                session.Dispose();
            }
        }
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
                // WaitToReadAsync returns true whenever an item is available and
                // does NOT observe the window token in that case — a sustained
                // frame flood would starve the tick. Bound the drain by the
                // window ourselves so the orchestrator always runs on cadence.
                while (!window.IsCancellationRequested
                       && await _ingress.Reader.WaitToReadAsync(window.Token))
                {
                    while (!window.IsCancellationRequested
                           && _ingress.Reader.TryRead(out var item))
                    {
                        Dispatch(in item);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // window elapsed — fall through to tick
            }
            catch (Exception ex)
            {
                // A dispatch fault must not kill the single-writer loop — log and tick on.
                _logger?.LogError(ex, "Sensing ingest dispatch failed");
            }

            var now = _time.GetUtcNow();
            try
            {
                _orchestrator.Tick(now);

                // Throttled projection — the UI polls at ~3Hz; snapshotting every
                // 100ms tick would triple the allocation rate for no benefit.
                if (now - _lastSnapshotAt >= SnapshotInterval)
                {
                    _lastSnapshotAt = now;
                    Volatile.Write(ref _latestSnapshot, _orchestrator.CreateSnapshot(now));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Sensing tick failed");
            }
        }
    }

    private void Dispatch(in IngressItem item)
    {
        if (_portsSeen.TryAdd(item.Port, 0))
        {
            _logger?.LogInformation("First frame received from {Port} ({Kind})", item.Port, item.Frame.Kind);
        }

        var now = _time.GetUtcNow();
        switch (item.Frame.Kind)
        {
            case TelemetryKind.Csi:
                var sample = item.Frame.Sample;
                _orchestrator.OnAmplitudeSampleReceived(in sample);
                break;
            case TelemetryKind.Ack:
                _radio.NotifyAck(item.Port, item.Frame.Ack.Seq, item.Frame.Ack.Success, item.Frame.Ack.Reason);
                break;
            case TelemetryKind.Config:
                _orchestrator.RegisterExpectedNode(item.Frame.AnnouncedMac, now);
                break;
            case TelemetryKind.Heartbeat:
                _orchestrator.OnNodeHeartbeat(item.Frame.AnnouncedMac, now);
                break;
            case TelemetryKind.RfScan:
                _orchestrator.OnRfScanReceived(item.Frame.Scan, now);
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

                // Some drivers ignore the CancellationToken while ReadAsync is
                // blocked — disposing the stream closes the handle and forces
                // the read to return, so shutdown can never zombie-hold a port.
                using var cancelReg = ct.Register(() => stream.Dispose());

                // Egress for this port lives exactly as long as the connection.
                var s = stream;
                nodeAdapter = new NodeSerialAdapter(
                    (bytes, token) => s.WriteAsync(bytes, token), _time, _ackTimeout, _maxAttempts);
                _radio.RegisterNode(portName, nodeAdapter);

                await PumpStreamAsync(stream, portName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                // Stream disposed by the cancel registration — clean shutdown.
                break;
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                // Same — the port close surfaced as an IO failure mid-read.
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
        // The wire is binary-framed (magic + len + payload + crc), not NDJSON —
        // accumulate raw bytes and drain complete frames.
        var accum = new List<byte>(8192);
        var buf = new byte[4096];
        var loggedFirstBytes = false;

        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n == 0)
            {
                break; // stream closed
            }

            if (!loggedFirstBytes)
            {
                loggedFirstBytes = true;
                _logger?.LogInformation(
                    "First bytes from {Port}: {Preview}", portName, AsciiPreview(buf.AsSpan(0, n)));
            }

            accum.AddRange(buf.AsSpan(0, n).ToArray());
            SerialFrameCodec.DrainFrames(accum, p => Enqueue(p, portName));
        }
    }

    private static string AsciiPreview(ReadOnlySpan<byte> span)
    {
        var len = Math.Min(64, span.Length);
        var chars = new char[len];
        for (var i = 0; i < len; i++)
        {
            chars[i] = span[i] is >= 0x20 and < 0x7F ? (char)span[i] : '.';
        }

        return new string(chars);
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
