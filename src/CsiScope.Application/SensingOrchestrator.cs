using System.Collections.Immutable;
using CsiScope.Application.Ports;
using CsiScope.Domain.Baselining;
using CsiScope.Domain.Events;
using CsiScope.Domain.Model;
using CsiScope.Domain.Strategy;

namespace CsiScope.Application;

/// <summary>
/// Coordinates the sensing campaign: owns the <see cref="CampaignState"/>
/// aggregate, the per-link <see cref="LinkBaseline"/> entities, and the
/// internal environment map (<see cref="ChannelActivity"/> per channel).
///
/// CONCURRENCY MODEL — single-writer: <see cref="OnAmplitudeSampleReceived"/>
/// and <see cref="Tick"/> must run on the same thread (or be serialized
/// through a channel/actor loop). There are no locks by design — the
/// telemetry hot path mutates baselines in place, allocation-free.
///
/// Radio commands are issued fire-and-forget: the port is async because the
/// transport is, but the orchestrator never awaits it on the sensing path.
/// </summary>
public sealed class SensingOrchestrator
{
    private const int MaxFilterMacs = 8; // firmware mac_filter capacity

    private readonly IRadioCommandPort _radio;
    private readonly IAnomalySink _anomalySink;
    private readonly SensingThresholds _thresholds;
    private readonly BaselineTunables _tunables;
    private readonly ImmutableArray<MacAddress> _expectedNodes;

    private readonly Dictionary<LinkIdentity, LinkBaseline> _baselines = new();
    private readonly Dictionary<WifiChannel, ChannelActivity> _activity = new();
    private readonly Dictionary<MacAddress, int> _macTotals = new();

    // Multi-step plan execution (survey sweep or environment audit).
    private ScanPlan? _activePlan;
    private int _planIndex;
    private DateTimeOffset _dwellStartedAt;
    private WifiChannel? _auditReturnTo;

    // Frame-delta bookkeeping for confidence/liveness evaluation.
    private long _lastTargetFrames;
    private long _lastChannelFrames;
    private DateTimeOffset _lastEvalAt = DateTimeOffset.MinValue;

    public SensingOrchestrator(
        IRadioCommandPort radio,
        IAnomalySink anomalySink,
        ImmutableArray<MacAddress> expectedNodes,
        SensingThresholds? thresholds = null,
        BaselineTunables? tunables = null)
    {
        _radio = radio;
        _anomalySink = anomalySink;
        _expectedNodes = expectedNodes;
        _thresholds = thresholds ?? SensingThresholds.Default;
        _tunables = tunables ?? BaselineTunables.Default;
    }

    /// <summary>The campaign aggregate — mode, lock, and transition evidence.</summary>
    public CampaignState Campaign { get; } = new();

    /// <summary>
    /// Telemetry hot path — strictly synchronous, zero-allocation on repeat
    /// samples (dictionary hit + in-place Welford update). Routes the sample
    /// to its link baseline, records channel activity for the environment
    /// map, and returns the tripwire event when one fires.
    /// </summary>
    public AnomalyDetected? OnAmplitudeSampleReceived(in AmplitudeSample sample)
    {
        RecordActivity(sample.Link.Channel, sample.Link.Source, sample.Timestamp);

        if (!_baselines.TryGetValue(sample.Link, out var baseline))
        {
            baseline = new LinkBaseline(sample.Link, _tunables);
            _baselines.Add(sample.Link, baseline);
        }

        var anomaly = baseline.Observe(in sample);
        if (anomaly is not null)
        {
            _ = _anomalySink.PublishAsync(anomaly);
        }

        return anomaly;
    }

    /// <summary>
    /// One orchestration step, driven by an external timer. Advances any
    /// in-progress multi-step plan (survey sweep or audit); otherwise builds
    /// the context snapshot, runs the pure policy, applies the decision to
    /// the campaign, and dispatches hardware side effects.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        if (_activePlan is not null)
        {
            AdvancePlan(now);
            return;
        }

        var ctx = BuildContext(now);
        var decision = SensingPolicy.Decide(ctx, _thresholds);
        Campaign.Apply(decision, ctx);
        Dispatch(decision, now);

        // Still surveying with nothing running -> keep sweeping.
        if (_activePlan is null && Campaign.Mode == CampaignMode.Surveying)
        {
            BeginPlan(BuildSurveyPlan(), now, auditReturnTo: null);
        }
    }

    /// <summary>Domain events recorded since the last drain (lock acquired, confidence degraded).</summary>
    public IReadOnlyList<object> DrainEvents() => Campaign.DrainEvents();

    // ---- Hot-path helpers ----

    private void RecordActivity(WifiChannel channel, MacAddress source, DateTimeOffset at)
    {
        if (!_activity.TryGetValue(channel, out var activity))
        {
            activity = new ChannelActivity(channel, at);
            _activity.Add(channel, activity);
        }

        activity.Record(source, at);
        _macTotals[source] = _macTotals.TryGetValue(source, out var n) ? n + 1 : 1;
    }

    // ---- Context construction ----

    private SensingContext BuildContext(DateTimeOffset now)
    {
        var locked = Campaign.LockedChannel;
        var targetBaselines = new List<LinkBaseline>();
        long targetFrames = 0, channelFrames = 0;
        DateTimeOffset? lastFrame = null;

        foreach (var kv in _baselines)
        {
            var baseline = kv.Value;
            if (locked is not { } ch || baseline.Link.Channel != ch)
            {
                continue;
            }

            channelFrames += baseline.TotalFrames;
            if (lastFrame is null || baseline.LastFrameAt > lastFrame)
            {
                lastFrame = baseline.LastFrameAt;
            }

            if (Campaign.PrimaryTarget is { } target && baseline.Link.Source == target)
            {
                targetBaselines.Add(baseline);
                targetFrames += baseline.TotalFrames;
            }
        }

        double seconds = _lastEvalAt == DateTimeOffset.MinValue
            ? 1.0
            : Math.Max((now - _lastEvalAt).TotalSeconds, 0.001);
        // Deltas clamp at zero: baseline resets (acquisition/reacquire) zero
        // TotalFrames, which would otherwise produce negative deltas and make
        // ChannelLiveness falsely report dead air on a live channel.
        double observedPps = Math.Max(0, targetFrames - _lastTargetFrames) / seconds;
        long channelDelta = Math.Max(0, channelFrames - _lastChannelFrames);
        _lastTargetFrames = targetFrames;
        _lastChannelFrames = channelFrames;
        _lastEvalAt = now;

        return new SensingContext
        {
            Mode = Campaign.Mode,
            Now = now,
            ModeEnteredAt = Campaign.ModeEnteredAt,
            LockedChannel = locked,
            Confidence = ConfidenceEvaluator.EvaluateConfidence(
                targetBaselines, observedPps, _thresholds.ExpectedTargetPps, _thresholds.StaleAfter, now),
            Liveness = locked is { } lc
                ? ConfidenceEvaluator.EvaluateLiveness(lc, channelDelta, lastFrame)
                : null,
            AllNodesConverged = AllNodesConverged(locked),
            Candidates = BuildCandidates(now),
            MacFilter = Campaign.MacFilter.Length > 0 ? Campaign.MacFilter : TopMacs(MaxFilterMacs),
            LastAuditAt = Campaign.LastAuditAt,
        };
    }

    private bool AllNodesConverged(WifiChannel? locked)
    {
        if (locked is not { } channel || _expectedNodes.IsEmpty)
        {
            return false;
        }

        foreach (var node in _expectedNodes)
        {
            var found = false;
            foreach (var kv in _baselines)
            {
                if (kv.Key.Node == node && kv.Key.Channel == channel && kv.Value.IsConverged)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private ImmutableArray<ChannelCandidate> BuildCandidates(DateTimeOffset now)
    {
        var list = new List<ChannelCandidate>(_activity.Count);
        foreach (var kv in _activity)
        {
            double pps = kv.Value.Pps(now);
            if (pps > 0)
            {
                list.Add(new ChannelCandidate(kv.Key, kv.Value.TopMac, new ActivityScore(pps)));
            }
        }

        list.Sort((a, b) => b.ActivityScore.CompareTo(a.ActivityScore));
        return list.ToImmutableArray();
    }

    private ImmutableArray<MacAddress> TopMacs(int take)
    {
        return _macTotals
            .OrderByDescending(kv => kv.Value)
            .Take(take)
            .Select(kv => kv.Key)
            .ToImmutableArray();
    }

    // ---- Decision dispatch ----

    private void Dispatch(SensingDecision decision, DateTimeOffset now)
    {
        switch (decision)
        {
            case SensingDecision.BeginAcquisition b:
                // Fresh baselines for the new lock — pre-lock frames are stale.
                ResetBaselinesOnChannel(b.Channel);
                HopTo(b.Channel, now, b.MacFilter);
                break;

            case SensingDecision.BeginSurvey s:
                BeginPlan(s.Plan, now, auditReturnTo: null);
                break;

            case SensingDecision.AuditChannels a:
                BeginPlan(a.Plan, now, a.ReturnTo);
                break;

            case SensingDecision.Reacquire:
                if (Campaign.LockedChannel is { } locked)
                {
                    ResetBaselinesOnChannel(locked);
                }

                break;

            case SensingDecision.ResumeSurveying:
            case SensingDecision.EnterDetecting:
            case SensingDecision.Hold:
                break;
        }
    }

    private void ResetBaselinesOnChannel(WifiChannel channel)
    {
        foreach (var kv in _baselines)
        {
            if (kv.Key.Channel == channel)
            {
                kv.Value.Reset();
            }
        }
    }

    // ---- Multi-step plan execution (survey sweeps and audits) ----

    private void BeginPlan(ScanPlan plan, DateTimeOffset now, WifiChannel? auditReturnTo)
    {
        if (plan.Channels.IsEmpty)
        {
            return;
        }

        _activePlan = plan;
        _planIndex = 0;
        _auditReturnTo = auditReturnTo;
        HopTo(plan.Channels[0], now, ImmutableArray<MacAddress>.Empty);
    }

    private void AdvancePlan(DateTimeOffset now)
    {
        var plan = _activePlan!;
        if (now - _dwellStartedAt < plan.DwellPerChannel)
        {
            return;
        }

        _planIndex++;
        if (_planIndex < plan.Channels.Length)
        {
            HopTo(plan.Channels[_planIndex], now, ImmutableArray<MacAddress>.Empty);
            return;
        }

        // Plan exhausted — audits return to the locked channel.
        _activePlan = null;
        if (_auditReturnTo is { } returnTo)
        {
            _auditReturnTo = null;
            HopTo(returnTo, now, Campaign.MacFilter);
        }
    }

    private void HopTo(WifiChannel channel, DateTimeOffset now, ImmutableArray<MacAddress> macFilter)
    {
        _dwellStartedAt = now;
        if (_activity.TryGetValue(channel, out var activity))
        {
            activity.ResetWindow(now);
        }
        else
        {
            _activity[channel] = new ChannelActivity(channel, now);
        }

        _ = _radio.BroadcastSetRfAsync(channel, macFilter);
    }

    private ScanPlan BuildSurveyPlan()
    {
        var channels = new List<WifiChannel>();
        foreach (var kv in _activity.OrderByDescending(kv => kv.Value.Frames))
        {
            channels.Add(kv.Key);
        }

        // Always include the non-overlapping standards so a quiet map still sweeps.
        foreach (var standard in new[] { 1, 6, 11 })
        {
            var channel = new WifiChannel(standard);
            if (!channels.Contains(channel))
            {
                channels.Add(channel);
            }
        }

        return new ScanPlan(channels.ToImmutableArray(), _thresholds.SurveyDwell);
    }
}
