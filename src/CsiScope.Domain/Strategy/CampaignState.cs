using CsiScope.Domain.Events;
using CsiScope.Domain.Model;

namespace CsiScope.Domain.Strategy;

/// <summary>
/// Campaign aggregate root: the only place orchestration state mutates.
/// A mode transition is atomic with the evidence that caused it — applied
/// exclusively through <see cref="Apply"/>, which also records the domain
/// events the transition implies.
/// </summary>
public sealed class CampaignState
{
    private readonly List<object> _pendingEvents = new();

    public CampaignMode Mode { get; private set; } = CampaignMode.Surveying;

    /// <summary>Locked channel while Acquiring/Detecting; null while Surveying.</summary>
    public WifiChannel? LockedChannel { get; private set; }

    /// <summary>First MAC in the filter — the primary confidence target.</summary>
    public MacAddress? PrimaryTarget { get; private set; }

    public IReadOnlyList<MacAddress> MacFilter { get; private set; } = Array.Empty<MacAddress>();

    public DateTimeOffset ModeEnteredAt { get; private set; }

    /// <summary>Last environment audit issued while Detecting.</summary>
    public DateTimeOffset? LastAuditAt { get; private set; }

    /// <summary>
    /// Applies a policy decision against the context that produced it.
    /// Audits stamp <see cref="LastAuditAt"/> without leaving Detecting —
    /// baselines are never torn down by an audit.
    /// </summary>
    public void Apply(SensingDecision decision, SensingContext ctx)
    {
        switch (decision)
        {
            case SensingDecision.BeginAcquisition b:
                Mode = CampaignMode.Acquiring;
                LockedChannel = b.Channel;
                MacFilter = b.MacFilter;
                PrimaryTarget = b.MacFilter.Count > 0 ? b.MacFilter[0] : null;
                ModeEnteredAt = ctx.Now;
                if (PrimaryTarget is { } target)
                {
                    _pendingEvents.Add(new ChannelLockAcquired(b.Channel, target, ctx.Now));
                }

                break;

            case SensingDecision.EnterDetecting:
                Mode = CampaignMode.Detecting;
                ModeEnteredAt = ctx.Now;
                break;

            case SensingDecision.ResumeSurveying r:
                if (LockedChannel is { } abandoned)
                {
                    _pendingEvents.Add(new ConfidenceDegraded(abandoned, ctx.Confidence, r.Reason, ctx.Now));
                }

                Mode = CampaignMode.Surveying;
                LockedChannel = null;
                PrimaryTarget = null;
                ModeEnteredAt = ctx.Now;
                break;

            case SensingDecision.Reacquire:
                // Keep the lock and filter — only the baseline window resets.
                Mode = CampaignMode.Acquiring;
                ModeEnteredAt = ctx.Now;
                break;

            case SensingDecision.AuditChannels:
                LastAuditAt = ctx.Now;
                break;

            case SensingDecision.Hold:
            case SensingDecision.BeginSurvey:
                break;
        }
    }

    /// <summary>Returns and clears events recorded since the last drain.</summary>
    public IReadOnlyList<object> DrainEvents()
    {
        var drained = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return drained;
    }
}
