using System.Buffers;
using System.Diagnostics;

namespace CsiHub.Core;

/// <summary>
/// Per-node baseline statistics for an interleaved I/Q CSI stream.
/// Computes Welford running mean/variance and an Exponential Moving Average (EMA)
/// independently for each I/Q sample over a fixed-size rolling window.
/// Buffers are sized by the active Wi-Fi bandwidth (56 subcarriers for HT20,
/// 114 for HT40) and reused without per-payload allocation.
/// </summary>
public sealed class RoomBaseline
{
    public const int DefaultWindowSize = 64;
    public const double DefaultSampleRateHz = 50.0;

    /// <summary>
    /// Scale factor that maps raw 8-bit signed I/Q samples to the [-1.0, 1.0] range.
    /// </summary>
    public const double CsiInputScale = 1.0 / 128.0;

    /// <summary>
    /// Convergence threshold for the 95th-percentile slot variance when inputs are scaled by
    /// <see cref="CsiInputScale"/>. A value of 0.01 corresponds to a raw variance of ~163,
    /// which is low compared to the full-scale raw variance of ~4000.
    /// </summary>
    public const double ConvergenceVarianceThreshold = 0.01;

    private readonly double _emaAlpha;
    private readonly double _sampleRateHz;

    private long[] _counts = Array.Empty<long>();
    private double[] _welfordMean = Array.Empty<double>();
    private double[] _welfordM2 = Array.Empty<double>();
    private double[] _variance = Array.Empty<double>();
    private double[] _ema = Array.Empty<double>();
    private double[] _ringBuffer = Array.Empty<double>();

    private int _bandwidth;
    private int _subcarrierCount;
    private int _windowSize;
    private int _slotCount;
    private int _head;
    private long _totalFrames;
    private bool _isInitialized;

    public RoomBaseline(double emaAlpha = 0.2, double sampleRateHz = DefaultSampleRateHz)
    {
        if (emaAlpha is <= 0.0 or >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(emaAlpha), "EMA alpha must be in (0, 1).");
        }

        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "Sample rate must be positive.");
        }

        _emaAlpha = emaAlpha;
        _sampleRateHz = sampleRateHz;
    }

    /// <summary>
    /// The MAC address this baseline belongs to.
    /// </summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>
    /// The active Wi-Fi bandwidth in MHz (20 or 40) used to size the buffers.
    /// </summary>
    public int Bandwidth => _bandwidth;

    /// <summary>
    /// The configured rolling window size in frames.
    /// </summary>
    public int WindowSize => _windowSize;

    /// <summary>
    /// Number of subcarriers implied by the bandwidth.
    /// </summary>
    public int SubcarrierCount => _subcarrierCount;

    /// <summary>
    /// True once <see cref="Initialize(int, int)"/> or <see cref="InitializeFromLength(int, int)"/> has been called.
    /// </summary>
    public bool IsInitialized => _slotCount > 0;

    /// <summary>
    /// Total number of CSI frames added to the baseline.
    /// </summary>
    public long TotalFrames => _totalFrames;

    /// <summary>
    /// Mean magnitude across all I/Q slots.
    /// </summary>
    public double MeanIq => ComputeMeanIq();

    /// <summary>
    /// Maximum population variance across all I/Q slots.
    /// </summary>
    public double MaxSlotVariance => ComputeMaxVariance();

    /// <summary>
    /// True once the rolling window has filled and the per-slot variance has stabilized.
    /// Uses the 95th-percentile slot variance so the noisiest 5% of subcarriers (guard/null
    /// carriers) do not prevent convergence.
    /// </summary>
    public bool IsConverged => _totalFrames >= _windowSize && ComputePercentileVariance(0.95) < ConvergenceVarianceThreshold;

    /// <summary>
    /// Running mean for every I/Q slot (length = 2 * SubcarrierCount).
    /// </summary>
    public Span<double> Mean => _welfordMean.AsSpan();

    /// <summary>
    /// Running population variance for every I/Q slot (length = 2 * SubcarrierCount).
    /// </summary>
    public Span<double> Variance => _variance.AsSpan();

    /// <summary>
    /// Exponential moving average for every I/Q slot (length = 2 * SubcarrierCount).
    /// </summary>
    public Span<double> Ema => _ema.AsSpan();

    /// <summary>
    /// Running mean as a <see cref="Memory{T}"/> for async or pinned access.
    /// </summary>
    public Memory<double> MeanMemory => _welfordMean.AsMemory();

    /// <summary>
    /// Running population variance as a <see cref="Memory{T}"/>.
    /// </summary>
    public Memory<double> VarianceMemory => _variance.AsMemory();

    /// <summary>
    /// Exponential moving average as a <see cref="Memory{T}"/>.
    /// </summary>
    public Memory<double> EmaMemory => _ema.AsMemory();

    /// <summary>
    /// Ensures buffers are sized for the given bandwidth and rolling window.
    /// </summary>
    /// <param name="bandwidth">Bandwidth in MHz (20 or 40).</param>
    /// <param name="windowSize">Number of frames to keep in the rolling window.</param>
    /// <param name="csiLength">
    /// Optional actual I/Q sample count from the CSI payload. When provided, buffers are sized
    /// to that exact length instead of the theoretical subcarrier count derived from bandwidth.
    /// </param>
    public void Initialize(int bandwidth, int windowSize = DefaultWindowSize, int? csiLength = null)
    {
        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");
        }

        if (csiLength.HasValue)
        {
            int length = csiLength.Value;
            if (length <= 0 || length % 2 != 0)
            {
                throw new ArgumentException("CSI array must contain an even number of interleaved I/Q samples.", nameof(csiLength));
            }
        }

        _bandwidth = bandwidth;
        _subcarrierCount = csiLength.HasValue ? csiLength.Value / 2 : GetSubcarrierCount(bandwidth);
        InitializeCore(windowSize);
    }

    /// <summary>
    /// Sizes buffers from the CSI frame length when the bandwidth is not known in advance.
    /// </summary>
    public void InitializeFromLength(int length, int windowSize = DefaultWindowSize)
    {
        if (length <= 0 || length % 2 != 0)
        {
            throw new ArgumentException("CSI array must contain an even number of interleaved I/Q samples.", nameof(length));
        }

        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");
        }

        _bandwidth = 0;
        _subcarrierCount = length / 2;
        InitializeCore(windowSize);
    }

    /// <summary>
    /// Updates the rolling-window Welford and EMA statistics with a new interleaved I/Q CSI frame.
    /// This method does not allocate: it only touches pre-allocated arrays and uses Span slicing.
    /// When <paramref name="dt"/> is supplied, the EMA smoothing is scaled by the elapsed time so
    /// that bursty, non-uniform telemetry is weighted correctly.
    /// </summary>
    public void Update(ReadOnlySpan<double> csi, TimeSpan? dt = null, double scale = 1.0)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Initialize must be called before Update.");
        }

        if (csi.Length != _slotCount)
        {
            throw new ArgumentException(
                $"CSI array length {csi.Length} does not match the baseline slot count {_slotCount}.",
                nameof(csi));
        }

        Debug.Assert(_ringBuffer.Length == _slotCount * _windowSize, "Ring buffer must be fully initialized.");

        // Evict the oldest frame if the window is already full.
        if (_totalFrames >= _windowSize)
        {
            var oldSpan = _ringBuffer.AsSpan(_head * _slotCount, _slotCount);

            for (int i = 0; i < _slotCount; i++)
            {
                var x = oldSpan[i];
                var n = _counts[i];

                if (n > 0)
                {
                    if (n == 1)
                    {
                        _counts[i] = 0;
                        _welfordMean[i] = 0.0;
                        _welfordM2[i] = 0.0;
                    }
                    else
                    {
                        var oldMean = _welfordMean[i];
                        var newMean = (n * oldMean - x) / (n - 1);
                        _welfordMean[i] = newMean;
                        _welfordM2[i] -= (x - oldMean) * (x - newMean);
                        _counts[i] = n - 1;
                    }
                }
            }
        }

        // Compute per-frame EMA alpha once; when dt is known, scale it by the
        // elapsed time relative to the nominal sample interval.
        double alpha = ComputeEmaAlpha(dt);
        double oneMinusAlpha = 1.0 - alpha;

        // Write the scaled new frame into the ring buffer and add it to the
        // running Welford and EMA statistics in one pass.
        var writeSpan = _ringBuffer.AsSpan(_head * _slotCount, _slotCount);
        for (int i = 0; i < _slotCount; i++)
        {
            double x = csi[i] * scale;
            writeSpan[i] = x;

            var n = _counts[i] + 1;
            _counts[i] = n;

            var oldMean = _welfordMean[i];
            var delta = x - oldMean;
            var newMean = oldMean + delta / n;
            _welfordMean[i] = newMean;
            _welfordM2[i] += delta * (x - newMean);
            _variance[i] = _welfordM2[i] / n;

            _ema[i] = n == 1 ? x : (alpha * x) + (oneMinusAlpha * _ema[i]);
        }

        _head = (_head + 1) % _windowSize;
        _totalFrames++;
    }

    private double ComputeEmaAlpha(TimeSpan? dt)
    {
        if (!dt.HasValue || dt.Value <= TimeSpan.Zero)
        {
            return _emaAlpha;
        }

        // Convert the fixed per-sample alpha to a time-constant so that
        // EMA smoothing scales with the actual inter-arrival interval.
        // alpha(dt) = 1 - (1 - alpha)^(dt * sampleRate)
        double intervals = dt.Value.TotalSeconds * _sampleRateHz;
        return 1.0 - Math.Pow(1.0 - _emaAlpha, intervals);
    }

    private double ComputeMeanIq()
    {
        if (_slotCount == 0)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int i = 0; i < _slotCount; i++)
        {
            sum += _welfordMean[i];
        }
        return sum / _slotCount;
    }

    private double ComputeMaxVariance()
    {
        if (_slotCount == 0)
        {
            return 0.0;
        }

        double max = 0.0;
        for (int i = 0; i < _slotCount; i++)
        {
            if (_variance[i] > max)
            {
                max = _variance[i];
            }
        }
        return max;
    }

    private double ComputePercentileVariance(double percentile)
    {
        if (_slotCount == 0)
        {
            return 0.0;
        }

        percentile = Math.Clamp(percentile, 0.0, 1.0);

        var scratch = ArrayPool<double>.Shared.Rent(_slotCount);
        try
        {
            _variance.AsSpan().CopyTo(scratch);
            Array.Sort(scratch, 0, _slotCount);

            double index = percentile * (_slotCount - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);

            if (lower == upper)
            {
                return scratch[lower];
            }

            double fraction = index - lower;
            return scratch[lower] + fraction * (scratch[upper] - scratch[lower]);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(scratch, clearArray: false);
        }
    }

    /// <summary>
    /// Maps bandwidth in MHz to the expected number of subcarriers.
    /// </summary>
    public static int GetSubcarrierCount(int bandwidth) => bandwidth switch
    {
        20 => 56,
        40 => 114,
        _ => throw new ArgumentOutOfRangeException(nameof(bandwidth), $"Unsupported bandwidth {bandwidth} MHz.")
    };

    private void InitializeCore(int windowSize)
    {
        _windowSize = windowSize;
        _slotCount = _subcarrierCount * 2;
        _head = 0;
        _totalFrames = 0;

        _counts = new long[_slotCount];
        _welfordMean = new double[_slotCount];
        _welfordM2 = new double[_slotCount];
        _variance = new double[_slotCount];
        _ema = new double[_slotCount];
        _ringBuffer = new double[_slotCount * windowSize];
        _isInitialized = true;
    }
}
