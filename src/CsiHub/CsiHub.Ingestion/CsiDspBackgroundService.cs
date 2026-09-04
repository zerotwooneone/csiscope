using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CsiHub.Core;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptionsMonitor<CsiAoaOptions> _aoaOptions;
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), RoomBaseline> _baselines = new();
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), DateTimeOffset> _lastUpdateAt = new();
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), (Complex Sample, DateTimeOffset At)> _latestSamples = new();
    private readonly ConcurrentDictionary<ulong, AoaEstimator.AoaResult> _aoaResults = new();
    private readonly TimeSpan _pruneInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _baselineMaxAge = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastPrune = DateTimeOffset.UtcNow;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public CsiDspBackgroundService(
        CsiIngestionChannel channel,
        ILogger<CsiDspBackgroundService> logger,
        IOptionsMonitor<CsiAoaOptions> aoaOptions)
    {
        _channel = channel;
        _logger = logger;
        _aoaOptions = aoaOptions;
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

    /// <summary>
    /// Per-target AoA estimates computed from the latest per-node CSI snapshots.
    /// </summary>
    public IReadOnlyDictionary<ulong, AoaEstimator.AoaResult> AoaResults => _aoaResults;

    /// <summary>
    /// Last status message from the AoA update pipeline, useful for diagnosing
    /// why the MUSIC estimator did not run for a target.
    /// </summary>
    public string LastAoaStatus { get; private set; } = "No AoA data yet";

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
                    var aoaOptions = _aoaOptions.CurrentValue;
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
                    else if (payload.Bandwidth.HasValue && csiBaseline.Bandwidth != payload.Bandwidth.Value)
                    {
                        // Only reinitialize on a true bandwidth change. Minor null-subcarrier
                        // stripping is handled by padding/truncating in RoomBaseline.Update.
                        csiBaseline.Initialize(payload.Bandwidth.Value, RoomBaseline.DefaultWindowSize, payload.Csi.Length);
                    }

                    csiBaseline.ConvergenceVarianceMultiplier = aoaOptions.ConvergenceVarianceMultiplier;

                    TimeSpan? dt = null;
                    if (_lastUpdateAt.TryGetValue(key, out var last))
                    {
                        dt = payload.ReceivedAt - last;
                    }

                    csiBaseline.Update(payload.Csi, dt, RoomBaseline.CsiInputScale);
                    csiBaseline.LastSeen = payload.ReceivedAt;
                    _lastUpdateAt[key] = payload.ReceivedAt;

                    _latestSamples[key] = (GetSubcarrierSample(payload.Csi, aoaOptions.SubcarrierIndex), payload.ReceivedAt);
                    TryUpdateAoa(key.SrcMac, payload.ReceivedAt, aoaOptions);
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

    private void TryUpdateAoa(ulong srcMac, DateTimeOffset now, CsiAoaOptions aoaOptions)
    {
        if (aoaOptions.SensorPositions.Count == 0)
        {
            LastAoaStatus = "No geometry configured";
            return;
        }

        var sensors = new List<AoaEstimator.SensorPosition>(aoaOptions.SensorPositions.Count);
        var samples = new List<Complex>(aoaOptions.SensorPositions.Count);

        foreach (var (nodeMac, position) in aoaOptions.SensorPositions)
        {
            if (!_latestSamples.TryGetValue((nodeMac, srcMac), out var entry))
            {
                LastAoaStatus = $"Waiting for node {nodeMac}";
                return;
            }

            if (now - entry.At > aoaOptions.SampleMaxAge)
            {
                LastAoaStatus = $"Stale sample from {nodeMac} ({(now - entry.At).TotalSeconds:F1}s old)";
                return;
            }

            sensors.Add(position);
            samples.Add(entry.Sample);
        }

        if (sensors.Count < aoaOptions.SourceCount + 1)
        {
            LastAoaStatus = $"Need {aoaOptions.SourceCount + 1} sensors, have {sensors.Count}";
            return;
        }

        double wavelength = aoaOptions.SpeedOfLight / aoaOptions.CarrierFrequencyHz;
        var snapshots = new[] { samples.ToArray() };

        var result = AoaEstimator.Estimate(
            sensors,
            snapshots,
            wavelength,
            aoaOptions.SourceCount,
            aoaOptions.StepDegrees);

        if (result is null)
        {
            LastAoaStatus = "Estimator returned null";
            return;
        }

        _aoaResults[srcMac] = result;
        LastAoaStatus = $"Updated AoA for {FormatMac(srcMac)}";
    }

    private Complex GetSubcarrierSample(double[]? csi, int subcarrierIndex)
    {
        if (csi is null || csi.Length < 2)
        {
            return Complex.Zero;
        }

        int subcarrierCount = csi.Length / 2;
        int index = Math.Clamp(subcarrierIndex, 0, subcarrierCount - 1);

        double real = csi[index * 2];
        double imag = csi[(index * 2) + 1];
        return new Complex(real, imag);
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
