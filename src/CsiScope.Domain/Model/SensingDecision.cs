using System.Collections.Immutable;

namespace CsiScope.Domain.Model;

/// <summary>
/// Discriminated result of <c>SensingPolicy.Decide</c> — the only way the
/// domain tells the outside world what to do next. Closed hierarchy.
/// </summary>
public abstract record SensingDecision
{
    private SensingDecision()
    {
    }

    /// <summary>No change — continue current behavior.</summary>
    public sealed record Hold : SensingDecision;

    /// <summary>Begin (or continue) a survey sweep over the given plan.</summary>
    public sealed record BeginSurvey(ScanPlan Plan) : SensingDecision;

    /// <summary>
    /// Lock a channel and acquire baselines for the MAC filter. The filter is
    /// an <see cref="ImmutableArray{T}"/> with sequence equality — identical
    /// decisions compare equal regardless of array instance.
    /// </summary>
    public sealed record BeginAcquisition(WifiChannel Channel, ImmutableArray<MacAddress> MacFilter) : SensingDecision
    {
        public bool Equals(BeginAcquisition? other) =>
            other is not null
            && Channel == other.Channel
            && MacFilter.SequenceEqual(other.MacFilter);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Channel);
            foreach (var mac in MacFilter)
            {
                hash.Add(mac);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>All nodes converged — enter anomaly detection.</summary>
    public sealed record EnterDetecting : SensingDecision;

    /// <summary>Abandon the lock and return to surveying.</summary>
    public sealed record ResumeSurveying(SurveyReason Reason) : SensingDecision;

    /// <summary>Stay on the channel but rebuild baselines (partial decay).</summary>
    public sealed record Reacquire : SensingDecision;

    /// <summary>
    /// Temporary off-channel audit while remaining in Detecting — baselines
    /// are NOT torn down; the application returns to <paramref name="ReturnTo"/>
    /// after the plan completes.
    /// </summary>
    public sealed record AuditChannels(ScanPlan Plan, WifiChannel ReturnTo) : SensingDecision;
}
