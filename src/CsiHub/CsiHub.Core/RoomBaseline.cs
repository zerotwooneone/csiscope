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

    private readonly double _emaAlpha;

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

    public RoomBaseline(double emaAlpha = 0.2)
    {
        if (emaAlpha is <= 0.0 or >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(emaAlpha), "EMA alpha must be in (0, 1).");
        }

        _emaAlpha = emaAlpha;
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
    public void Initialize(int bandwidth, int windowSize = DefaultWindowSize)
    {
        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");
        }

        _bandwidth = bandwidth;
        _subcarrierCount = GetSubcarrierCount(bandwidth);
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
    /// </summary>
    public void Update(double[] csi)
    {
        if (csi is null)
        {
            throw new ArgumentNullException(nameof(csi));
        }

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

        var csiSpan = csi.AsSpan();

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

        // Write the new frame into the circular ring buffer.
        var writeSpan = _ringBuffer.AsSpan(_head * _slotCount, _slotCount);
        csiSpan.CopyTo(writeSpan);
        _head = (_head + 1) % _windowSize;
        _totalFrames++;

        // Add the new frame to the running Welford and EMA statistics.
        for (int i = 0; i < _slotCount; i++)
        {
            var x = csiSpan[i];
            var n = _counts[i] + 1;
            _counts[i] = n;

            var oldMean = _welfordMean[i];
            var delta = x - oldMean;
            var newMean = oldMean + delta / n;
            _welfordMean[i] = newMean;
            _welfordM2[i] += delta * (x - newMean);
            _variance[i] = _welfordM2[i] / n;

            _ema[i] = n == 1 ? x : (_emaAlpha * x) + ((1.0 - _emaAlpha) * _ema[i]);
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
