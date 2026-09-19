namespace CsiScope.Domain.Model;

/// <summary>
/// Composite baseline confidence for the primary target MAC's links. Each
/// component is clamped to 0..1 and individually inspectable for diagnostics;
/// the composite <see cref="Value"/> is their product so any collapsed
/// component drags the whole score down.
/// </summary>
public readonly record struct ConfidenceScore
{
    public ConfidenceScore(double windowFill, double floorStability, double ingestionRate, double freshness)
    {
        WindowFill = Clamp01(windowFill);
        FloorStability = Clamp01(floorStability);
        IngestionRate = Clamp01(ingestionRate);
        Freshness = Clamp01(freshness);
    }

    /// <summary>Fraction of the Welford window filled (0..1).</summary>
    public double WindowFill { get; }

    /// <summary>Variance-floor stability: 1 = flat/decaying, 0 = drifting.</summary>
    public double FloorStability { get; }

    /// <summary>Ingestion rate vs expected, normalized (0..1).</summary>
    public double IngestionRate { get; }

    /// <summary>Recency of the last frame (1 = just now, 0 = stale).</summary>
    public double Freshness { get; }

    public double Value => WindowFill * FloorStability * IngestionRate * Freshness;

    public static ConfidenceScore Zero => new(0, 0, 0, 0);

    public static ConfidenceScore Full => new(1, 1, 1, 1);

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}
