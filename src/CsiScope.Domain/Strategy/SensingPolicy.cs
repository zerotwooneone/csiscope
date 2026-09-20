using System.Collections.Immutable;
using CsiScope.Domain.Model;

namespace CsiScope.Domain.Strategy;

/// <summary>
/// The autonomous orchestration policy — a pure, deterministic function from
/// <see cref="SensingContext"/> to <see cref="SensingDecision"/>. No timers,
/// no I/O, no hardware: every transition is testable as data-in/decision-out.
/// </summary>
public static class SensingPolicy
{
    public static SensingDecision Decide(SensingContext ctx, SensingThresholds? thresholds = null)
    {
        var t = thresholds ?? SensingThresholds.Default;
        return ctx.Mode switch
        {
            CampaignMode.Surveying => DecideSurveying(ctx, t),
            CampaignMode.Acquiring => DecideAcquiring(ctx, t),
            CampaignMode.Detecting => DecideDetecting(ctx, t),
            _ => new SensingDecision.Hold(),
        };
    }

    // Surveying: acquire the best candidate that clears the activity floor;
    // otherwise keep sweeping.
    private static SensingDecision DecideSurveying(SensingContext ctx, SensingThresholds t)
    {
        ChannelCandidate? best = null;
        foreach (var c in ctx.Candidates)
        {
            if (c.ActivityScore >= t.MinActivityScore
                && (best is null || c.ActivityScore > best.Value.ActivityScore))
            {
                best = c;
            }
        }

        // Filter for the WINNING channel's own top MAC — ctx.MacFilter is the
        // global top set across all channels, so using it can lock channel A
        // while targeting a MAC that only transmits on channel B (dead air).
        return best is { } b
            ? new SensingDecision.BeginAcquisition(
                b.Channel,
                b.TopMac == default ? ctx.MacFilter : [b.TopMac])
            : new SensingDecision.Hold();
    }

    // Acquiring: converge -> Detecting. Dead air -> fast skip back to survey.
    // The dwell is progress-adaptive: while the target keeps producing frames
    // the baseline is still refining, so hold. When the target stalls (quiet but
    // the channel is alive), go listen for alternatives WITHOUT releasing the
    // lock — the audit returns to the locked channel. Give up only when a stall
    // outlasts the cap, or the absolute dwell ceiling is hit.
    private static SensingDecision DecideAcquiring(SensingContext ctx, SensingThresholds t)
    {
        if (ctx.AllNodesConverged)
        {
            return new SensingDecision.EnterDetecting();
        }

        var dwell = ctx.Now - ctx.ModeEnteredAt;
        bool dead = ctx.Liveness is null or { IsAlive: false };
        if (dead && dwell > t.DeadChannelTimeout)
        {
            return new SensingDecision.ResumeSurveying(SurveyReason.DeadAir);
        }

        // Absolute ceiling — even a "progressing" acquisition can't dwell forever.
        if (dwell > t.MaxAcquisitionDwell)
        {
            return new SensingDecision.ResumeSurveying(SurveyReason.AcquisitionTimeout);
        }

        bool stalled = ctx.StalledFor > t.StallTimeout;
        if (stalled)
        {
            // Past the normal cap AND still stalled — the target isn't coming back.
            if (dwell > t.AcquisitionTimeout)
            {
                return new SensingDecision.ResumeSurveying(SurveyReason.AcquisitionTimeout);
            }

            // Stalled but within budget: spend the dwell listening for
            // alternatives, then return to the lock and give it another window.
            var plan = BuildAuditPlan(ctx, t);
            if (!plan.Channels.IsEmpty)
            {
                return new SensingDecision.AuditChannels(plan, ctx.LockedChannel!.Value);
            }
        }

        return new SensingDecision.Hold();
    }

    // Detecting: collapsed confidence -> abandon (dead air = fast skip, live
    // channel = target moved, hunt it). Degraded -> rebuild baselines in
    // place. Otherwise audit the environment on a confidence-scaled cadence.
    private static SensingDecision DecideDetecting(SensingContext ctx, SensingThresholds t)
    {
        double score = ctx.Confidence.Value;
        bool alive = ctx.Liveness is { IsAlive: true };

        if (score < t.AbandonThreshold)
        {
            return new SensingDecision.ResumeSurveying(
                alive ? SurveyReason.TargetShifted : SurveyReason.DeadAir);
        }

        if (score < t.ReacquireThreshold)
        {
            return new SensingDecision.Reacquire();
        }

        if (ctx.LockedChannel is { } returnTo && AuditDue(ctx, score, t))
        {
            var plan = BuildAuditPlan(ctx, t);
            if (!plan.Channels.IsEmpty)
            {
                return new SensingDecision.AuditChannels(plan, returnTo);
            }
        }

        return new SensingDecision.Hold();
    }

    // Audit cadence scales inversely with confidence: full confidence waits
    // MaxAuditInterval; at the reacquire boundary it tightens to MinAuditInterval.
    private static bool AuditDue(SensingContext ctx, double confidence, SensingThresholds t)
    {
        var interval = t.MinAuditInterval
            + ((t.MaxAuditInterval - t.MinAuditInterval) * confidence);
        var since = ctx.Now - (ctx.LastAuditAt ?? ctx.ModeEnteredAt);
        return since >= interval;
    }

    // Audit sweeps every surveyed channel except the locked one.
    private static ScanPlan BuildAuditPlan(SensingContext ctx, SensingThresholds t)
    {
        var channels = new List<WifiChannel>();
        foreach (var c in ctx.Candidates)
        {
            if (ctx.LockedChannel is { } locked && c.Channel == locked)
            {
                continue;
            }

            if (!channels.Contains(c.Channel))
            {
                channels.Add(c.Channel);
            }
        }

        return new ScanPlan(channels.ToImmutableArray(), t.AuditDwell);
    }
}
