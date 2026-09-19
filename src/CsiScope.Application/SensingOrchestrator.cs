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

    // Consecutive wrong-channel frames before the last hop is declared lost
    // and reissued — absorbs in-flight stragglers from the previous channel.
    private const int ChannelMismatchThreshold = 8;

    private static readonly TimeSpan DormancyThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    // A node silent this long is unregistered so it can't stall Acquiring.
    private static readonly TimeSpan NodeLivenessTimeout = TimeSpan.FromSeconds(10);

    private readonly IRadioCommandPort _radio;
    private readonly IAnomalySink _anomalySink;
    private readonly SensingThresholds _thresholds;
    private readonly BaselineTunables _tunables;
    private readonly HashSet<MacAddress> _expectedNodes;
    private readonly Dictionary<MacAddress, DateTimeOffset> _nodeLastSeen = new();
    private DateTimeOffset _firstTickAt = DateTimeOffset.MinValue;

    private readonly Dictionary<LinkIdentity, LinkBaseline> _baselines = new();
    private readonly Dictionary<WifiChannel, ChannelActivity> _activity = new();
    private readonly Dictionary<MacAddress, (int Count, DateTimeOffset LastSeenAt)> _macTotals = new();

    // Channel-confirmation: what the radio was last told to do. Sustained
    // contradiction in incoming telemetry means the command was dropped.
    private WifiChannel? _believedChannel;
    private ImmutableArray<MacAddress> _believedFilter = [];
    private int _channelMismatches;
    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;

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
        _expectedNodes = new HashSet<MacAddress>(expectedNodes);
        _thresholds = thresholds ?? SensingThresholds.Default;
        _tunables = tunables ?? BaselineTunables.Default;
    }

    /// <summary>The campaign aggregate — mode, lock, and transition evidence.</summary>
    public CampaignState Campaign { get; } = new();

    /// <summary>Live baseline count — diagnostics and pruning verification.</summary>
    public int TrackedBaselineCount => _baselines.Count;

    /// <summary>Registered node count — diagnostics and watchdog verification.</summary>
    public int ExpectedNodeCount => _expectedNodes.Count;

    /// <summary>
    /// Register a node that self-announced via a config frame and seed its
    /// liveness clock. Single-writer: call from the ingestion/tick thread.
    /// </summary>
    public void RegisterExpectedNode(MacAddress node, DateTimeOffset at)
    {
        _expectedNodes.Add(node);
        _nodeLastSeen[node] = at;
    }

    /// <summary>Drop a node from the roster — watchdog or explicit disconnect.</summary>
    public void UnregisterExpectedNode(MacAddress node)
    {
        _expectedNodes.Remove(node);
        _nodeLastSeen.Remove(node);
    }

    /// <summary>Heartbeat frame — the node is alive; (re)register and touch liveness.</summary>
    public void OnNodeHeartbeat(MacAddress node, DateTimeOffset at)
    {
        _expectedNodes.Add(node);
        _nodeLastSeen[node] = at;
    }

    /// <summary>
    /// Telemetry hot path — strictly synchronous, zero-allocation on repeat
    /// samples (dictionary hit + in-place Welford update). Routes the sample
    /// to its link baseline, records channel activity for the environment
    /// map, and returns the tripwire event when one fires.
    /// </summary>
    public AnomalyDetected? OnAmplitudeSampleReceived(in AmplitudeSample sample)
    {
        // Front-door gatekeeper: multicast and locally-administered
        // (randomized probe-request) MACs never reach baselines or the
        // environment map — they churn constantly and would leak memory.
        if (sample.Link.Source.IsMulticast || sample.Link.Source.IsLocallyAdministered)
        {
            return null;
        }

        // A node producing telemetry is alive — (re)register and touch liveness.
        _expectedNodes.Add(sample.Link.Node);
        _nodeLastSeen[sample.Link.Node] = sample.Timestamp;

        // Channel-confirmation: sustained contradiction means the last hop
        // command was dropped — reissue it. A dead radio produces no frames
        // at all, which the dead-air policy path already handles.
        if (_believedChannel is { } expected)
        {
            if (sample.Link.Channel == expected)
            {
                _channelMismatches = 0;
            }
            else if (++_channelMismatches >= ChannelMismatchThreshold)
            {
                _channelMismatches = 0;
                _ = _radio.BroadcastSetRfAsync(expected, _believedFilter);
            }
        }

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
        // Housekeeping runs every tick — even mid-plan. A silent node must be
        // unregistered during sweeps too, or it stalls the next acquisition.
        if (_firstTickAt == DateTimeOffset.MinValue)
        {
            _firstTickAt = now;
        }

        // Dormancy pruning, throttled — dictionaries can't be modified during
        // enumeration, so stale keys are collected first (cold-path alloc).
        if (now - _lastPruneAt > PruneInterval)
        {
            _lastPruneAt = now;
            PruneDormant(now);
        }

        RunNodeWatchdog(now);

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

    /// <summary>
    /// Unregisters nodes silent for <see cref="NodeLivenessTimeout"/>. Nodes
    /// configured but never seen get a grace period measured from the first tick.
    /// </summary>
    private void RunNodeWatchdog(DateTimeOffset now)
    {
        List<MacAddress>? deadNodes = null;
        foreach (var node in _expectedNodes)
        {
            var lastSeen = _nodeLastSeen.TryGetValue(node, out var seen) ? seen : _firstTickAt;
            if (now - lastSeen > NodeLivenessTimeout)
            {
                (deadNodes ??= new List<MacAddress>()).Add(node);
            }
        }

        if (deadNodes is not null)
        {
            foreach (var node in deadNodes)
            {
                UnregisterExpectedNode(node);
            }
        }
    }

    private void PruneDormant(DateTimeOffset now)
    {
        List<LinkIdentity>? staleLinks = null;
        foreach (var kv in _baselines)
        {
            if (now - kv.Value.LastFrameAt > DormancyThreshold)
            {
                (staleLinks ??= new List<LinkIdentity>()).Add(kv.Key);
            }
        }

        if (staleLinks is not null)
        {
            foreach (var key in staleLinks)
            {
                _baselines.Remove(key);
            }
        }

        List<MacAddress>? staleMacs = null;
        foreach (var kv in _macTotals)
        {
            if (now - kv.Value.LastSeenAt > DormancyThreshold)
            {
                (staleMacs ??= new List<MacAddress>()).Add(kv.Key);
            }
        }

        if (staleMacs is not null)
        {
            foreach (var key in staleMacs)
            {
                _macTotals.Remove(key);
            }
        }
    }

    /// <summary>Domain events recorded since the last drain (lock acquired, confidence degraded).</summary>
    public IReadOnlyList<object> DrainEvents() => Campaign.DrainEvents();

    /// <summary>
    /// Projects internal state into an immutable <see cref="SensingSnapshot"/>
    /// for UI/diagnostic consumers. Drains pending campaign events into
    /// <see cref="SensingSnapshot.RecentEvents"/> so they can't accumulate.
    ///
    /// SINGLE-WRITER ONLY — call from the ingestion/tick thread. Enumerating
    /// the private dictionaries from any other thread races with mutation.
    /// </summary>
    public SensingSnapshot CreateSnapshot(DateTimeOffset now)
    {
        var baselines = ImmutableArray.CreateBuilder<BaselineReadModel>(_baselines.Count);
        foreach (var kv in _baselines)
        {
            var b = kv.Value;
            baselines.Add(new BaselineReadModel(
                b.Link, b.Mean, b.Floor, b.FillFraction, b.IsConverged, b.TotalFrames, b.LastFrameAt));
        }

        baselines.Sort((a, b) =>
        {
            var cmp = a.Link.Node.ToUInt64().CompareTo(b.Link.Node.ToUInt64());
            return cmp != 0 ? cmp : a.Link.Source.ToUInt64().CompareTo(b.Link.Source.ToUInt64());
        });

        var channels = ImmutableArray.CreateBuilder<ChannelActivityReadModel>(_activity.Count);
        foreach (var kv in _activity)
        {
            var a = kv.Value;
            channels.Add(new ChannelActivityReadModel(
                kv.Key, new ActivityScore(a.Pps(now)), a.TopMac, a.LastFrameAt));
        }

        channels.Sort((a, b) => b.Activity.CompareTo(a.Activity));

        var nodes = ImmutableArray.CreateBuilder<NodeLivenessReadModel>(_expectedNodes.Count);
        foreach (var node in _expectedNodes)
        {
            nodes.Add(new NodeLivenessReadModel(
                node, _nodeLastSeen.TryGetValue(node, out var seen) ? seen : null));
        }

        return new SensingSnapshot(
            now,
            Campaign.Mode,
            Campaign.ModeEnteredAt,
            Campaign.LockedChannel,
            Campaign.PrimaryTarget,
            baselines.MoveToImmutable(),
            channels.MoveToImmutable(),
            nodes.MoveToImmutable(),
            Campaign.DrainEvents().ToImmutableArray());
    }

    // ---- Hot-path helpers ----

    private void RecordActivity(WifiChannel channel, MacAddress source, DateTimeOffset at)
    {
        if (!_activity.TryGetValue(channel, out var activity))
        {
            activity = new ChannelActivity(channel, at);
            _activity.Add(channel, activity);
        }

        activity.Record(source, at);
        _macTotals[source] = _macTotals.TryGetValue(source, out var e) ? (e.Count + 1, at) : (1, at);
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
        if (locked is not { } channel || _expectedNodes.Count == 0)
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
        // Every known channel is a candidate — the policy's MinActivityScore
        // gate decides which are worth ACQUIRING, while audits need the full
        // list: off-lock channels receive no frames, so filtering on pps > 0
        // would starve audit plans entirely.
        var list = new List<ChannelCandidate>(_activity.Count);
        foreach (var kv in _activity)
        {
            list.Add(new ChannelCandidate(kv.Key, kv.Value.TopMac, new ActivityScore(kv.Value.Pps(now))));
        }

        list.Sort((a, b) => b.ActivityScore.CompareTo(a.ActivityScore));
        return list.ToImmutableArray();
    }

    private ImmutableArray<MacAddress> TopMacs(int take)
    {
        return _macTotals
            .OrderByDescending(kv => kv.Value.Count)
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

        // Rebase the delta counters: they tracked pre-reset sums, so the next
        // evaluation must measure only post-reset frames — otherwise the
        // delta goes negative and a live channel reads as dead air.
        _lastChannelFrames = 0;
        _lastTargetFrames = 0;
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
        _believedChannel = channel;
        _believedFilter = macFilter;
        _channelMismatches = 0;
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
