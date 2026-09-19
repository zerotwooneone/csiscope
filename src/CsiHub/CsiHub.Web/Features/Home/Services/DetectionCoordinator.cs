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

    // Maximum time to hold the static acquisition dwell waiting for every
    // node's baseline to converge before hopping anyway.
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(90);

    // How long acquisition waits on a channel with zero new frames before
    // skipping to the next candidate — prevents stalling on dead air.
    private static readonly TimeSpan DeadChannelTimeout = TimeSpan.FromSeconds(8);

    private readonly CsiNodeStateStore _store;
    private readonly CsiNodePortManager _portManager;
    private readonly CsiDspBackgroundService _dsp;
    private readonly ILogger<DetectionCoordinator> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DetectionCoordinator(
        CsiNodeStateStore store,
        CsiNodePortManager portManager,
        CsiDspBackgroundService dsp,
        ILogger<DetectionCoordinator> logger)
    {
        _store = store;
        _portManager = portManager;
        _dsp = dsp;
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
    /// True while the coordinator holds a static dwell on the first channel,
    /// waiting for every node's baseline to converge before hopping begins.
    /// </summary>
    public bool IsAcquiring { get; private set; }

    /// <summary>
    /// The last channel abandoned during acquisition because it produced no
    /// frames within <see cref="DeadChannelTimeout"/>, or null.
    /// </summary>
    public int? LastSkippedChannel { get; private set; }

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
            // Acquisition phase: hold a static dwell until every node locks a
            // baseline. Hopping during acquisition dilutes frame arrivals, but
            // holding a dead channel stalls forever — so channels with no new
            // frames are skipped after DeadChannelTimeout.
            IsAcquiring = true;
            var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;
            var acquireIndex = 0;
            var acquireChannel = channels[0];
            CurrentChannel = acquireChannel;
            BroadcastSetRf(acquireChannel, dwellMs, macFilter);
            var channelStartedAt = DateTimeOffset.UtcNow;
            var framesAtStart = FramesOnChannel(acquireChannel);

            while (!cancellationToken.IsCancellationRequested
                   && DateTimeOffset.UtcNow < deadline
                   && !AllNodesConverged(acquireChannel))
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow - channelStartedAt > DeadChannelTimeout
                    && FramesOnChannel(acquireChannel) == framesAtStart)
                {
                    // Dead air: no new frames on this channel — try the next.
                    var nextChannel = channels[(acquireIndex + 1) % channels.Length];
                    _logger.LogInformation(
                        "Acquisition: ch {Dead} produced no frames in {Timeout}s — skipping to ch {Next}.",
                        acquireChannel, DeadChannelTimeout.TotalSeconds, nextChannel);
                    LastSkippedChannel = acquireChannel;
                    acquireIndex = (acquireIndex + 1) % channels.Length;
                    acquireChannel = nextChannel;
                    CurrentChannel = acquireChannel;
                    BroadcastSetRf(acquireChannel, dwellMs, macFilter);
                    channelStartedAt = DateTimeOffset.UtcNow;
                    framesAtStart = FramesOnChannel(acquireChannel);
                }
            }

            IsAcquiring = false;
            if (!AllNodesConverged(acquireChannel))
            {
                _logger.LogWarning(
                    "Baseline acquisition timed out after {Timeout}s on ch {Channel}; starting hops anyway.",
                    AcquisitionTimeout.TotalSeconds, acquireChannel);
            }

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
            IsAcquiring = false;
            CurrentChannel = null;
            StartedAt = null;
            LastSkippedChannel = null;
        }
    }

    // Every connected node has at least one converged baseline link on the
    // given channel.
    private bool AllNodesConverged(int channel)
    {
        var nodes = _store.GetConnectedMacs();
        if (nodes.Count == 0)
        {
            return false;
        }

        foreach (var node in nodes)
        {
            // Baseline keys are canonical (colonless); connected MACs are
            // colon-delimited — normalize before comparing.
            var canonical = CsiHub.Core.MacAddressFormatter.ToCanonicalCached(node);
            bool nodeConverged = _dsp.Baselines.Any(kv =>
                kv.Key.Channel == channel
                && kv.Value.IsConverged
                && string.Equals(kv.Key.NodeMac, canonical, StringComparison.OrdinalIgnoreCase));
            if (!nodeConverged)
            {
                return false;
            }
        }

        return true;
    }

    // Total baseline frames recorded on a channel across all links — used to
    // detect dead-air channels during acquisition.
    private long FramesOnChannel(int channel)
        => _dsp.Baselines
            .Where(kv => kv.Key.Channel == channel)
            .Sum(kv => kv.Value.TotalFrames);

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
