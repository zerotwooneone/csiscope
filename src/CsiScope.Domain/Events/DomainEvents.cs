using CsiScope.Domain.Model;

namespace CsiScope.Domain.Events;

/// <summary>A channel lock was taken for acquisition/detection.</summary>
public sealed record ChannelLockAcquired(WifiChannel Channel, MacAddress PrimaryTarget, DateTimeOffset At);

/// <summary>
/// Per-link tripwire: squared amplitude deviation exceeded multiplier × floor.
/// Deliberately single-link — multi-link shadowing correlation is a later
/// consumer of this stream, not part of this layer.
/// Carries the raw amplitude and the floor it crossed so diagnostics can show
/// exactly why the tripwire fired, not just the ratio.
/// </summary>
public sealed record AnomalyDetected(
    LinkIdentity Link,
    DeviationRatio DeviationRatio,
    double RawAmplitude,
    double CurrentFloor,
    DateTimeOffset At);

/// <summary>The campaign abandoned a lock; <see cref="Reason"/> routes the decay path.</summary>
public sealed record ConfidenceDegraded(
    WifiChannel Channel,
    ConfidenceScore Score,
    SurveyReason Reason,
    DateTimeOffset At);
