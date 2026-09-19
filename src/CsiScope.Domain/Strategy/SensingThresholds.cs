namespace CsiScope.Domain.Strategy;

/// <summary>
/// Tunable policy parameters. Defaults mirror the proven host values
/// (8 s dead-air skip, 90 s acquisition cap, inverse-scaled audits).
/// </summary>
public sealed record SensingThresholds
{
    public static SensingThresholds Default { get; } = new();

    /// <summary>Acquisition dwell on a channel with zero new frames before skipping.</summary>
    public TimeSpan DeadChannelTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Maximum acquisition dwell before abandoning the channel.</summary>
    public TimeSpan AcquisitionTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Composite confidence below this abandons the lock entirely.</summary>
    public double AbandonThreshold { get; init; } = 0.15;

    /// <summary>Composite confidence below this triggers a baseline rebuild.</summary>
    public double ReacquireThreshold { get; init; } = 0.5;

    /// <summary>Minimum candidate activity score to justify acquisition.</summary>
    public double MinActivityScore { get; init; } = 1.0;

    /// <summary>Audit cadence at full confidence.</summary>
    public TimeSpan MaxAuditInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Audit cadence at the reacquire boundary.</summary>
    public TimeSpan MinAuditInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-channel dwell during an audit sweep.</summary>
    public TimeSpan AuditDwell { get; init; } = TimeSpan.FromMilliseconds(250);
}
