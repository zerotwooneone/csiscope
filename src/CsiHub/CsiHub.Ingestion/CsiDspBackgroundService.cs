using System.Collections.Concurrent;
using CsiHub.Core;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion;

/// <summary>
/// Hosted service that consumes the DSP payload channel and maintains per-node
/// <see cref="RoomBaseline"/> statistics. This is the hook point for raw individual
/// node processing before any cross-node fusion (such as the follower/leader CSI ratio).
/// </summary>
public sealed class CsiDspBackgroundService : IHostedService, IAsyncDisposable
{
    private readonly CsiIngestionChannel _channel;
    private readonly ILogger<CsiDspBackgroundService> _logger;
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), RoomBaseline> _baselines = new();
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), DateTimeOffset> _lastUpdateAt = new();
    private readonly TimeSpan _pruneInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _baselineMaxAge = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastPrune = DateTimeOffset.UtcNow;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public CsiDspBackgroundService(CsiIngestionChannel channel, ILogger<CsiDspBackgroundService> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => ProcessDspAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_task is not null)
        {
            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("DSP pipeline did not stop within the timeout.");
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Snapshot of the current baselines for inspection or downstream fusion.
    /// </summary>
    public IReadOnlyDictionary<(string NodeMac, ulong SrcMac), RoomBaseline> Baselines => _baselines;

    private async Task ProcessDspAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _channel.DspPayloadReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(payload.Mac))
                {
                    continue;
                }

                var key = (NodeMac: payload.Mac!, SrcMac: payload.SrcMac ?? 0UL);

                if (payload.Type == "csi" && payload.Csi is not null)
                {
                    var csiBaseline = _baselines.GetOrAdd(
                        key,
                        _ => new RoomBaseline { Mac = FormatMac(key.SrcMac) });

                    if (!csiBaseline.IsInitialized)
                    {
                        if (payload.Bandwidth.HasValue)
                        {
                            csiBaseline.Initialize(payload.Bandwidth.Value, RoomBaseline.DefaultWindowSize, payload.Csi.Length);
                        }
                        else
                        {
                            csiBaseline.InitializeFromLength(payload.Csi.Length, RoomBaseline.DefaultWindowSize);
                        }
                    }
                    else if (payload.Bandwidth.HasValue &&
                             (csiBaseline.Bandwidth != payload.Bandwidth.Value ||
                              csiBaseline.SubcarrierCount * 2 != payload.Csi.Length))
                    {
                        csiBaseline.Initialize(payload.Bandwidth.Value, RoomBaseline.DefaultWindowSize, payload.Csi.Length);
                    }
                    else if (csiBaseline.SubcarrierCount * 2 != payload.Csi.Length)
                    {
                        csiBaseline.InitializeFromLength(payload.Csi.Length, RoomBaseline.DefaultWindowSize);
                    }

                    TimeSpan? dt = null;
                    if (_lastUpdateAt.TryGetValue(key, out var last))
                    {
                        dt = payload.ReceivedAt - last;
                    }

                    csiBaseline.Update(payload.Csi, dt, RoomBaseline.CsiInputScale);
                    _lastUpdateAt[key] = payload.ReceivedAt;
                }

                if (DateTimeOffset.UtcNow - _lastPrune > _pruneInterval)
                {
                    PruneOldBaselines(DateTimeOffset.UtcNow);
                    _lastPrune = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private void PruneOldBaselines(DateTimeOffset now)
    {
        foreach (var key in _lastUpdateAt.Keys)
        {
            if (_lastUpdateAt.TryGetValue(key, out var last) &&
                now - last > _baselineMaxAge &&
                _baselines.TryRemove(key, out _))
            {
                _lastUpdateAt.TryRemove(key, out _);
            }
        }
    }

    private static string FormatMac(ulong mac)
    {
        return $"{(mac >> 40) & 0xFF:X2}:{(mac >> 32) & 0xFF:X2}:{(mac >> 24) & 0xFF:X2}:{(mac >> 16) & 0xFF:X2}:{(mac >> 8) & 0xFF:X2}:{mac & 0xFF:X2}";
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
