namespace CsiScope.Domain.Baselining;

/// <summary>
/// Welford baseline configuration — groups every tunable so no constants
/// are hardcoded inside <see cref="LinkBaseline"/>.
/// </summary>
public sealed record BaselineTunables
{
    public const int DefaultWindowSize = 64;

    /// <summary>Frames required to fill the Welford window before the floor locks.</summary>
    public int WindowSize { get; init; } = DefaultWindowSize;

    /// <summary>Convergence threshold = multiplier × locked floor.</summary>
    public double ConvergenceMultiplier { get; init; } = 1.5;

    /// <summary>Tripwire fires when squared deviation exceeds multiplier × floor.</summary>
    public double TripwireMultiplier { get; init; } = 2.5;

    /// <summary>Minimum gap between emitted anomalies per link.</summary>
    public TimeSpan TripwireCooldown { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sustained above-threshold evaluations before the floor re-locks at the
    /// new level — prevents a quiet-transient latch from locking out forever.
    /// </summary>
    public int RelockAfterMisses { get; init; } = 128;

    public static BaselineTunables Default { get; } = new();
}
