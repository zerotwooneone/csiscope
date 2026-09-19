using System.Collections.Immutable;
using CsiScope.Domain.Model;

namespace CsiScope.Domain.Strategy;

/// <summary>
/// Immutable evaluation snapshot handed to <see cref="SensingPolicy.Decide"/>.
/// The application layer composes this from campaign state, baseline
/// telemetry, and the environment map — the policy itself never reaches out.
/// </summary>
public sealed record SensingContext
{
    public required CampaignMode Mode { get; init; }

    /// <summary>Evaluation instant — all timing derives from this, never a clock.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>When the current mode was entered (dwell = Now - ModeEnteredAt).</summary>
    public DateTimeOffset ModeEnteredAt { get; init; }

    /// <summary>The locked channel while Acquiring/Detecting.</summary>
    public WifiChannel? LockedChannel { get; init; }

    /// <summary>Primary-target confidence (fill × stability × target PPS × age).</summary>
    public ConfidenceScore Confidence { get; init; } = ConfidenceScore.Zero;

    /// <summary>Aggregate channel activity floor — null means "no data yet" (treated as dead).</summary>
    public ChannelLiveness? Liveness { get; init; }

    /// <summary>Every rostered node has at least one converged link on the locked channel.</summary>
    public bool AllNodesConverged { get; init; }

    /// <summary>Survey-ranked acquisition candidates, best activity first.</summary>
    public ImmutableArray<ChannelCandidate> Candidates { get; init; } = [];

    /// <summary>Firmware MAC filter for acquisition/lock commands.</summary>
    public ImmutableArray<MacAddress> MacFilter { get; init; } = [];

    /// <summary>Last environment audit; null falls back to ModeEnteredAt.</summary>
    public DateTimeOffset? LastAuditAt { get; init; }
}
