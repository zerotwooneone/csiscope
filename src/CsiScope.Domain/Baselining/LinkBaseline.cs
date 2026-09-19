using CsiScope.Domain.Events;
using CsiScope.Domain.Model;

namespace CsiScope.Domain.Baselining;

/// <summary>
/// Per-link baseline entity: Welford amplitude statistics, convergence latch
/// with adaptive re-lock, and tripwire evaluation. The consistency boundary
/// is exactly one link — no cross-link invariants, so each instance mutates
/// independently with no locking.
/// </summary>
public sealed class LinkBaseline
{
    public const int DefaultWindowSize = 64;

    // Sustained above-threshold evaluations before the floor re-locks at the
    // new level — prevents a quiet-transient latch from locking out forever.
    private const int RelockAfterMisses = 128;

    private static readonly TimeSpan TripwireCooldown = TimeSpan.FromSeconds(1);

    private readonly int _windowSize;
    private readonly double _convergenceMultiplier;
    private readonly double _tripwireMultiplier;
    private double _mean;
    private double _m2;
    private double _varianceFloor;
    private double _convergenceThreshold;
    private bool _thresholdLocked;
    private int _convergenceMisses;
    private DateTimeOffset _lastTripwireAt = DateTimeOffset.MinValue;

    public LinkBaseline(
        LinkIdentity link,
        int windowSize = DefaultWindowSize,
        double convergenceMultiplier = 1.5,
        double tripwireMultiplier = 2.5)
    {
        if (windowSize < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window must hold at least 2 frames.");
        }

        Link = link;
        _windowSize = windowSize;
        _convergenceMultiplier = convergenceMultiplier;
        _tripwireMultiplier = tripwireMultiplier;
    }

    public LinkIdentity Link { get; }

    public long TotalFrames { get; private set; }

    public DateTimeOffset LastFrameAt { get; private set; } = DateTimeOffset.MinValue;

    public double Mean => _mean;

    public double Variance => BaselineEstimator.Variance(_m2, TotalFrames);

    public VarianceFloor Floor => new(_varianceFloor);

    public double ConvergenceThreshold => _convergenceThreshold;

    public int WindowSize => _windowSize;

    /// <summary>Fraction of the Welford window filled (0..1).</summary>
    public double FillFraction => Math.Min(1.0, (double)TotalFrames / _windowSize);

    /// <summary>
    /// True once the window is full and variance sits under the locked
    /// threshold. Evaluation has side effects: the first evaluation past the
    /// window locks the floor, and sustained elevation adaptively re-locks it.
    /// </summary>
    public bool IsConverged => EvaluateConvergence();

    /// <summary>
    /// Ingest one scalar amplitude reading — O(1), zero-alloc hot path.
    /// Returns an <see cref="AnomalyDetected"/> when a converged baseline
    /// trips (squared deviation &gt; multiplier × floor), honoring a per-link
    /// cooldown so a sustained disturbance doesn't flood the event stream.
    /// </summary>
    public AnomalyDetected? Observe(in AmplitudeSample sample)
    {
        // Drive the floor-lock / adaptive re-lock lifecycle every frame, then
        // evaluate the tripwire against the PRE-update baseline: an anomaly is
        // a deviation from what was already learned. The gate is the locked
        // floor, not current stability — an absorbed spike inflates variance
        // and flips IsConverged off, which must not disable the detector.
        AnomalyDetected? anomaly = null;
        EvaluateConvergence();
        if (_thresholdLocked)
        {
            double deviation = sample.Amplitude - _mean;
            double squared = deviation * deviation;
            double ratio = _varianceFloor > 1e-12 ? squared / _varianceFloor : 0.0;

            if (ratio > _tripwireMultiplier
                && sample.Timestamp - _lastTripwireAt > TripwireCooldown)
            {
                _lastTripwireAt = sample.Timestamp;
                anomaly = new AnomalyDetected(Link, ratio, sample.Timestamp);
            }
        }

        TotalFrames++;
        LastFrameAt = sample.Timestamp;
        (_mean, _m2) = BaselineEstimator.Update(_mean, _m2, TotalFrames, sample.Amplitude);
        return anomaly;
    }

    // Convergence lifecycle: locks the floor on the first evaluation past the
    // window; sustained elevation re-locks it instead of latching out forever.
    private bool EvaluateConvergence()
    {
        if (TotalFrames < _windowSize)
        {
            return false;
        }

        double variance = Variance;
        if (!_thresholdLocked)
        {
            _varianceFloor = variance;
            _convergenceThreshold = _convergenceMultiplier * variance;
            _thresholdLocked = true;
        }

        if (variance <= _convergenceThreshold)
        {
            _convergenceMisses = 0;
            return true;
        }

        if (++_convergenceMisses >= RelockAfterMisses)
        {
            _varianceFloor = variance;
            _convergenceThreshold = _convergenceMultiplier * variance;
            _convergenceMisses = 0;
        }

        return false;
    }
}
