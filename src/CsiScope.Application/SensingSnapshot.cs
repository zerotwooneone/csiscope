using System.Collections.Immutable;
using CsiScope.Domain.Baselining;
using CsiScope.Domain.Model;
using CsiScope.Domain.Strategy;

namespace CsiScope.Application;

/// <summary>
/// Immutable point-in-time projection of the orchestrator's state for UI
/// consumption. Produced by <see cref="SensingOrchestrator.CreateSnapshot"/>
/// on the single-writer thread; safe to read from any thread once published.
/// </summary>
public sealed record SensingSnapshot(
    DateTimeOffset TakenAt,
    CampaignMode Mode,
    DateTimeOffset ModeEnteredAt,
    WifiChannel? LockedChannel,
    MacAddress? PrimaryTarget,
    ImmutableArray<BaselineReadModel> Baselines,
    ImmutableArray<ChannelActivityReadModel> ChannelScores,
    ImmutableArray<NodeLivenessReadModel> NodeLiveness,
    ImmutableArray<object> RecentEvents,
    ConfidenceScore Confidence,
    double TargetPps,
    int ConvergedNodes,
    int ExpectedNodes,
    TimeSpan StalledFor,
    DateTimeOffset? LastAuditAt);

/// <summary>Per-link baseline health for the diagnostics table.</summary>
public readonly record struct BaselineReadModel(
    LinkIdentity Link,
    double Mean,
    VarianceFloor CurrentFloor,
    double FillFraction,
    bool IsConverged,
    long TotalFrames,
    DateTimeOffset LastFrameAt);

/// <summary>Per-channel environment-map entry, ranked by activity.</summary>
public readonly record struct ChannelActivityReadModel(
    WifiChannel Channel,
    ActivityScore Activity,
    MacAddress TopMac,
    DateTimeOffset LastFrameAt);

/// <summary>Node roster entry — LastSeen is null for configured-but-never-seen.</summary>
public readonly record struct NodeLivenessReadModel(
    MacAddress Node,
    DateTimeOffset? LastSeen);
