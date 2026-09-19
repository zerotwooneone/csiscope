using CsiScope.Domain.Model;

namespace CsiScope.Domain.Strategy;

/// <summary>
/// Tunable policy parameters. Defaults mirror the proven host values
/// (8 s dead-air skip, 90 s acquisition cap, inverse-scaled audits).
/// </summary>
/// <remarks>
/// Lessons carried over from the legacy RfChannelEvaluator, for when candidate
/// scoring grows beyond raw activity: a transmitter below ~50 pps starves the
/// pipeline (legacy applied a 0.1× score penalty); RSSI stability was scored
/// 100/(1+spread/10); MACs with low amplitude variance were preferred via
/// 1/(1+ampVar) since they can hold a baseline; and a sqrt(packets)/100
/// congestion bonus broke ties toward busier channels. Packet-weighted
/// variance merging combined dwells: (v1·n1 + v2·n2)/(n1+n2).
/// </remarks>
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
    public ActivityScore MinActivityScore { get; init; } = new(1.0);

    /// <summary>Expected packets-per-second for the primary target — normalizes the TargetPps confidence component.</summary>
    public double ExpectedTargetPps { get; init; } = 5.0;

    /// <summary>Time without frames before the Age confidence component reaches zero.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-channel dwell during a survey sweep.</summary>
    public TimeSpan SurveyDwell { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Audit cadence at full confidence.</summary>
    public TimeSpan MaxAuditInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Audit cadence at the reacquire boundary.</summary>
    public TimeSpan MinAuditInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-channel dwell during an audit sweep.</summary>
    public TimeSpan AuditDwell { get; init; } = TimeSpan.FromMilliseconds(250);
}
