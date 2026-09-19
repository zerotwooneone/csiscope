using System.Text.Json;
using CsiHub.Ingestion;
using Microsoft.Extensions.Logging;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Drives synchronized channel hopping across the whole array: every node is
/// sent the same <c>set_rf</c> passive-streaming command, the coordinator waits
/// for the dwell plus a small slack for raw CSI telemetry to flow back through
/// ingestion, then advances all nodes to the next channel in the curated list.
/// Repeats until stopped. Passive mode keeps nodes in STATE_STREAMING so the
/// DSP tripwire engine sees every CSI frame; unlike
/// <see cref="CsiNodeStateStore.StartDistributedSweepAsync"/>, which gives each
/// node an independent queue, this keeps the array locked step so all nodes
/// measure the same channel at the same time.
/// </summary>
/// <remarks>
/// Lives in the Web layer rather than CsiHub.Ingestion because it needs
/// <see cref="CsiNodeStateStore.GetConnectedMacs"/> and
/// <see cref="CsiNodeStateStore.TryGetPortName"/> for MAC-to-port routing.
/// </remarks>
public sealed class DetectionCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Extra milliseconds added to each dwell so the node's telemetry payload
    /// has time to be emitted, framed, and ingested before the next hop.
    /// </summary>
    public const int SlackMs = 50;

    private readonly CsiNodeStateStore _store;
    private readonly CsiNodePortManager _portManager;
    private readonly ILogger<DetectionCoordinator> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DetectionCoordinator(
        CsiNodeStateStore store,
        CsiNodePortManager portManager,
        ILogger<DetectionCoordinator> logger)
    {
        _store = store;
        _portManager = portManager;
        _logger = logger;
    }

    /// <summary>
    /// True while a detection loop is running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// The channel the array is currently dwelling on, or null when idle.
    /// </summary>
    public int? CurrentChannel { get; private set; }

    /// <summary>
    /// When the current detection loop was started, or null when idle.
    /// </summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    /// Starts cycling the array through <paramref name="channels"/>, dwelling
    /// <paramref name="dwellMs"/> per hop, streaming raw CSI filtered to
    /// <paramref name="macFilter"/>. Any running loop is replaced.
    /// </summary>
    public void Start(int[] channels, int dwellMs, string[] macFilter)
    {
        if (channels.Length == 0)
        {
            throw new ArgumentException("At least one channel is required.", nameof(channels));
        }

        if (macFilter.Length == 0)
        {
            // Firmware rejects set_rf passive with missing_mac_filter.
            throw new ArgumentException("At least one target MAC is required.", nameof(macFilter));
        }

        Stop();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            IsRunning = true;
            StartedAt = DateTimeOffset.UtcNow;
            _loop = Task.Run(() => RunAsync(channels, dwellMs, macFilter, cts.Token));
        }

        _logger.LogInformation(
            "Detection coordinator started: {Count} channels, {Dwell}ms dwell, {Macs} target MAC(s).",
            channels.Length, dwellMs, macFilter.Length);
    }

    /// <summary>
    /// Stops the detection loop. In-flight dwells finish their delay via
    /// cancellation; nodes simply stop receiving hop commands.
    /// </summary>
    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            IsRunning = false;
            CurrentChannel = null;
            StartedAt = null;
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
            _cts = null;
        }

        // Observe the task so a faulted loop does not surface as an
        // unobserved exception; do not block the caller on it.
        if (loop is not null)
        {
            _ = loop.ContinueWith(
                t => _logger.LogWarning(t.Exception, "Detection loop faulted."),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task RunAsync(int[] channels, int dwellMs, string[] macFilter, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var channel in channels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CurrentChannel = channel;
                    BroadcastSetRf(channel, dwellMs, macFilter);

                    // Dwell plus slack: raw CSI streams continuously during the
                    // dwell; the slack lets the last frames clear ingestion
                    // before the next hop retunes the radio.
                    await Task.Delay(dwellMs + SlackMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on Stop().
        }
        finally
        {
            IsRunning = false;
            CurrentChannel = null;
            StartedAt = null;
        }
    }

    private void BroadcastSetRf(int channel, int dwellMs, string[] macFilter)
    {
        var json = JsonSerializer.Serialize(new
        {
            cmd = "set_rf",
            ch = channel,
            bw = 20,
            mode = "passive",
            mac_filter = macFilter
        });

        foreach (var mac in _store.GetConnectedMacs())
        {
            if (!_store.TryGetPortName(mac, out var portName) || string.IsNullOrWhiteSpace(portName))
            {
                _logger.LogWarning("Skipping {Mac} for ch {Channel}: port not found.", mac, channel);
                continue;
            }

            if (!_portManager.TrySendCommand(portName, json))
            {
                _logger.LogWarning("Failed to queue set_rf ch {Channel} for {Mac} on {Port}.", channel, mac, portName);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
