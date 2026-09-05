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
    private readonly ConcurrentDictionary<string, double[]> _latestImu = new();

    // Dead-CSI diagnostic: scans every frame's full subcarrier array to find
    // whether any I/Q data is present and, if so, at which subcarrier indices.
    private long _csiFrames;
    private long _csiAllZeroFrames;
    private long _csiNonZeroElements;
    private double _csiMaxMagnitudeSq;
    private int _csiFirstNonZero = -1;
    private int _csiLastNonZero = -1;
    private readonly double[] _csiIndexEnergy = new double[256]; // per-subcarrier accumulated |s|^2
    private readonly long[] _csiIndexLive = new long[256];       // per-subcarrier nonzero-frame count
    private int _refIndex = -1;        // most-reliably-live subcarrier, locked after warmup
    private long _cfgNonZero;         // frames where the configured subcarrier is nonzero
    private long _fwReportedFrames;   // frames that carried a firmware nz field
    private long _fwNonZeroFrames;    // frames where firmware reported nz > 0
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), PhaseCoherence> _phaseCoherence = new();
    private const int PhaseHistoryCapacity = 128;
    private readonly ConcurrentDictionary<(string NodeMac, ulong SrcMac), PhaseHistoryBuffer> _phaseHistory = new();
    private bool _subcarrierWarned;
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
    /// Latest IMU quaternion per node, stored as [w, x, y, z].
    /// </summary>
    public IReadOnlyDictionary<string, double[]> LatestImu => _latestImu;

    /// <summary>
    /// Last status message from the AoA update pipeline, useful for diagnosing
    /// why the MUSIC estimator did not run for a target.
    /// </summary>
    public string LastAoaStatus { get; private set; } = "No AoA data yet";

    /// <summary>
    /// Whole-array CSI health: how many frames are entirely zero versus carry
    /// I/Q data, the peak amplitude seen, and the subcarrier index range that
    /// has ever held a nonzero value. Distinguishes a dead subcarrier index
    /// from an empty CSI payload.
    /// </summary>
    public string SubcarrierDiag
    {
        get
        {
            long frames = Interlocked.Read(ref _csiFrames);
            if (frames == 0)
            {
                return "No CSI samples yet";
            }

            long allZero = Interlocked.Read(ref _csiAllZeroFrames);
            long cfgNz = Interlocked.Read(ref _cfgNonZero);

            // Strongest subcarriers by accumulated energy - candidate AoA indices.
            var top = _csiIndexEnergy
                .Select((energy, i) => (energy, i))
                .Where(x => x.energy > 0.0)
                .OrderByDescending(x => x.energy)
                .Take(3)
                .Select(x => x.i)
                .ToArray();
            var bestStr = string.Join(",", top);

            // Per-(node,source) phase coherence: R near 1 means the phase is
            // stable frame-to-frame (a constant offset, which is calibratable);
            // R near 0 means it is random per packet (independent PLL/detection
            // timing), which makes coherent cross-node AoA infeasible.
            var coh = string.Join(",", _phaseCoherence.Select(kv => kv.Value.R.ToString("F2")));

            var diag = $"live {frames - allZero}/{frames} · cfg nz {cfgNz}/{frames} · ref {_refIndex} · top [{bestStr}] · coh [{coh}]";

            // Compare what the firmware counted against what the host received.
            long fwRep = Interlocked.Read(ref _fwReportedFrames);
            if (fwRep > 0)
            {
                diag += $" · fw nz>0 {Interlocked.Read(ref _fwNonZeroFrames)}/{fwRep}";
            }

            return diag;
        }
    }

    /// <summary>
    /// The locked reference subcarrier index used for the phase-coherence and
    /// phase-history diagnostics, or -1 until it locks after warmup.
    /// </summary>
    public int ReferenceIndex => _refIndex;

    /// <summary>
    /// Snapshot of the recent raw phase angles (radians, -pi..+pi) at the locked
    /// reference subcarrier for each (node, source) pair, oldest to newest. The
    /// UI plots these to visualize packet-to-packet phase jitter directly.
    /// </summary>
    public IReadOnlyList<PhaseSeries> GetPhaseSeries()
    {
        var list = new List<PhaseSeries>(_phaseHistory.Count);
        foreach (var kv in _phaseHistory)
        {
            var phases = kv.Value.Snapshot();
            if (phases.Length > 0)
            {
                list.Add(new PhaseSeries(kv.Key.NodeMac, FormatMac(kv.Key.SrcMac), _refIndex, phases));
            }
        }

        return list;
    }

    /// <summary>
    /// One node's raw phase time-series at the reference subcarrier.
    /// </summary>
    public sealed record PhaseSeries(string NodeMac, string SrcMac, int SubcarrierIndex, double[] Phases);

    private async Task ProcessDspAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _channel.DspPayloadReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // A single malformed payload must not kill the DSP loop; without
                // this catch the task faults silently and processing stops.
                try
                {
                    if (string.IsNullOrWhiteSpace(payload.Mac))
                    {
                        continue;
                    }

                    var rawMac = payload.Mac!;
                    var key = (NodeMac: rawMac, SrcMac: payload.SrcMac ?? 0UL);
                    var sampleKey = (NodeMac: MacAddressFormatter.ToCanonical(rawMac), SrcMac: key.SrcMac);

                    if (payload.Type == "imu" && payload.Imu is not null)
                    {
                        _latestImu[rawMac] = payload.Imu;
                        continue;
                    }

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

                        var sample = GetSubcarrierSample(payload.Csi, aoaOptions.SubcarrierIndex);
                        ScanCsiHealth(payload.Csi, payload.CsiNonZero, aoaOptions.SubcarrierIndex, key);
                        _latestSamples[sampleKey] = (sample, payload.ReceivedAt);
                        TryUpdateAoa(key.SrcMac, payload.ReceivedAt, aoaOptions);
                    }

                    if (DateTimeOffset.UtcNow - _lastPrune > _pruneInterval)
                    {
                        PruneOldBaselines(DateTimeOffset.UtcNow);
                        _lastPrune = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to process {Type} payload from {Mac}.",
                        payload.Type,
                        payload.Mac ?? "unknown");
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

        foreach (var (configuredMac, position) in aoaOptions.SensorPositions)
        {
            var nodeMac = MacAddressFormatter.ToCanonical(configuredMac);
            if (!_latestSamples.TryGetValue((nodeMac, srcMac), out var entry))
            {
                LastAoaStatus = $"Waiting for node {configuredMac}";
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

    private void ScanCsiHealth(double[]? csi, int? firmwareNonZero, int configuredIndex, (string NodeMac, ulong SrcMac) key)
    {
        Interlocked.Increment(ref _csiFrames);

        // Track the firmware's own nonzero-byte count so we can tell an empty
        // on-node buffer apart from data lost between emit and parse.
        if (firmwareNonZero.HasValue)
        {
            Interlocked.Increment(ref _fwReportedFrames);
            if (firmwareNonZero.Value > 0)
            {
                Interlocked.Increment(ref _fwNonZeroFrames);
            }
        }

        if (csi is not { Length: >= 2 })
        {
            Interlocked.Increment(ref _csiAllZeroFrames);
            return;
        }

        // Configured-index liveness: is the AoA input actually nonzero?
        var cfgSample = GetSubcarrierSample(csi, configuredIndex);
        if (cfgSample != Complex.Zero)
        {
            Interlocked.Increment(ref _cfgNonZero);
        }

        // Scan the whole interleaved I/Q array so we can tell an empty payload
        // apart from data that simply lives at unexpected subcarrier indices.
        int subcarrierCount = csi.Length / 2;
        int frameNonZero = 0;
        int firstNz = -1;
        int lastNz = -1;
        double frameMaxSq = 0.0;

        for (int i = 0; i < subcarrierCount; i++)
        {
            double re = csi[i * 2];
            double im = csi[(i * 2) + 1];
            double magSq = (re * re) + (im * im);
            if (magSq <= 0.0)
            {
                continue;
            }

            frameNonZero++;
            if (firstNz < 0)
            {
                firstNz = i;
            }

            lastNz = i;
            if (magSq > frameMaxSq)
            {
                frameMaxSq = magSq;
            }

            if (i < _csiIndexEnergy.Length)
            {
                _csiIndexEnergy[i] += magSq;
                _csiIndexLive[i]++;
            }
        }

        if (frameNonZero == 0)
        {
            Interlocked.Increment(ref _csiAllZeroFrames);
        }
        else
        {
            Interlocked.Add(ref _csiNonZeroElements, frameNonZero);
            if (frameMaxSq > _csiMaxMagnitudeSq)
            {
                _csiMaxMagnitudeSq = frameMaxSq;
            }

            if (_csiFirstNonZero < 0 || firstNz < _csiFirstNonZero)
            {
                _csiFirstNonZero = firstNz;
            }

            if (lastNz > _csiLastNonZero)
            {
                _csiLastNonZero = lastNz;
            }
        }

        // Lock the reference subcarrier to the most-reliably-live index once we
        // have enough frames, then measure phase coherence there. This gives a
        // clean coherence reading on a strong, consistently-live tone instead of
        // whatever (possibly dead or marginal) index happens to be configured.
        if (_refIndex < 0 && Interlocked.Read(ref _csiFrames) >= 200)
        {
            int best = -1;
            long bestCount = 0;
            for (int i = 0; i < _csiIndexLive.Length; i++)
            {
                if (_csiIndexLive[i] > bestCount)
                {
                    bestCount = _csiIndexLive[i];
                    best = i;
                }
            }

            _refIndex = best;
        }

        if (_refIndex >= 0)
        {
            var refSample = GetSubcarrierSample(csi, _refIndex);
            if (refSample != Complex.Zero)
            {
                double phase = refSample.Phase;
                _phaseCoherence.GetOrAdd(key, _ => new PhaseCoherence()).Add(phase);
                _phaseHistory.GetOrAdd(key, _ => new PhaseHistoryBuffer(PhaseHistoryCapacity)).Add(phase);
            }
        }

        if (_subcarrierWarned)
        {
            return;
        }

        long frames = Interlocked.Read(ref _csiFrames);
        long allZero = Interlocked.Read(ref _csiAllZeroFrames);
        if (frames >= 100 && allZero == frames)
        {
            _subcarrierWarned = true;
            long fwNz = Interlocked.Read(ref _fwNonZeroFrames);
            if (fwNz > 0)
            {
                _logger.LogWarning(
                    "Firmware reported nonzero CSI bytes in {FwNz} frames but the host received all-zero arrays - CSI data is lost between emit and parse (framing/serialization).",
                    fwNz);
            }
            else
            {
                _logger.LogWarning(
                    "CSI payloads are entirely zero across {Frames} frames and the firmware reports no nonzero bytes - the node is not capturing CSI data. Check the CSI config and captured frame types.",
                    frames);
            }
        }
    }

    /// <summary>
    /// Exponentially-weighted mean of a unit phasor. The magnitude R in [0,1] is
    /// the phase coherence: near 1 means the phase is stable frame-to-frame (a
    /// constant offset, which is calibratable); near 0 means it is random per
    /// packet (independent PLL/detection timing), which makes coherent
    /// cross-node AoA infeasible.
    /// </summary>
    private sealed class PhaseCoherence
    {
        private const double Alpha = 0.9;
        private double _re;
        private double _im;

        public void Add(double phase)
        {
            _re = (Alpha * _re) + ((1.0 - Alpha) * Math.Cos(phase));
            _im = (Alpha * _im) + ((1.0 - Alpha) * Math.Sin(phase));
        }

        public double R => Math.Sqrt((_re * _re) + (_im * _im));
    }

    /// <summary>
    /// Fixed-capacity ring buffer of raw phase angles (radians). Snapshot returns
    /// the contents oldest-to-newest so the UI can plot them as a time series.
    /// </summary>
    private sealed class PhaseHistoryBuffer
    {
        private readonly double[] _buf;
        private readonly object _gate = new();
        private int _head;
        private int _count;

        public PhaseHistoryBuffer(int capacity)
        {
            _buf = new double[capacity];
        }

        public void Add(double phase)
        {
            lock (_gate)
            {
                _buf[_head] = phase;
                _head = (_head + 1) % _buf.Length;
                if (_count < _buf.Length)
                {
                    _count++;
                }
            }
        }

        public double[] Snapshot()
        {
            lock (_gate)
            {
                var result = new double[_count];
                int start = _count < _buf.Length ? 0 : _head;
                for (int i = 0; i < _count; i++)
                {
                    result[i] = _buf[(start + i) % _buf.Length];
                }

                return result;
            }
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
