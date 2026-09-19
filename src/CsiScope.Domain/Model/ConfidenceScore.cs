namespace CsiScope.Domain.Model;

/// <summary>
/// Composite baseline confidence for the primary target MAC's links. Each
/// component is clamped to 0..1 and individually inspectable for diagnostics;
/// the composite <see cref="Value"/> is their product so any collapsed
/// component drags the whole score down.
/// </summary>
public readonly record struct ConfidenceScore
{
    public ConfidenceScore(double fill, double stability, double targetPps, double age)
    {
        Fill = Clamp01(fill);
        Stability = Clamp01(stability);
        TargetPps = Clamp01(targetPps);
        Age = Clamp01(age);
    }

    /// <summary>Fraction of the Welford window filled (0..1).</summary>
    public double Fill { get; }

    /// <summary>Variance-floor stability: 1 = flat/decaying, 0 = drifting.</summary>
    public double Stability { get; }

    /// <summary>Primary-target packet rate vs expected, normalized (0..1).</summary>
    public double TargetPps { get; }

    /// <summary>Normalized recency of the last frame (1 = just observed, 0 = stale).</summary>
    public double Age { get; }

    public double Value => Fill * Stability * TargetPps * Age;

    public static ConfidenceScore Zero => new(0, 0, 0, 0);

    public static ConfidenceScore Full => new(1, 1, 1, 1);

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}
