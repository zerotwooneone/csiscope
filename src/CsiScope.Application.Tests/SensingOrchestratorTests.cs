using System.Collections.Immutable;
using CsiScope.Application.Ports;
using CsiScope.Domain.Baselining;
using CsiScope.Domain.Events;
using CsiScope.Domain.Model;
using CsiScope.Domain.Strategy;
using FluentAssertions;
using Xunit;

namespace CsiScope.Application.Tests;

public class SensingOrchestratorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");
    private static readonly ImmutableArray<MacAddress> Nodes = ImmutableArray.Create(
        MacAddress.Parse("14:C1:9F:2E:53:D0"),
        MacAddress.Parse("14:C1:9F:2E:53:D1"),
        MacAddress.Parse("14:C1:9F:2E:53:D2"));

    // Globally-administered unicast — survives the ingestion gatekeeper.
    private static readonly MacAddress OtherMac = MacAddress.Parse("AC:DE:48:00:11:22");

    // Tight timing so tests aren't pinned to production defaults.
    private static readonly SensingThresholds Fast = new()
    {
        SurveyDwell = TimeSpan.FromMilliseconds(100),
        AuditDwell = TimeSpan.FromMilliseconds(50),
        DeadChannelTimeout = TimeSpan.FromSeconds(2),
        MaxAuditInterval = TimeSpan.FromSeconds(5),
        MinAuditInterval = TimeSpan.FromSeconds(1),
    };

    private sealed class FakeRadio : IRadioCommandPort
    {
        public List<(WifiChannel Channel, ImmutableArray<MacAddress> Filter)> Calls { get; } = new();

        public Task<bool> BroadcastSetRfAsync(WifiChannel channel, ImmutableArray<MacAddress> macFilter, CancellationToken ct = default)
        {
            Calls.Add((channel, macFilter));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeSink : IAnomalySink
    {
        public List<AnomalyDetected> Anomalies { get; } = new();

        public ValueTask PublishAsync(AnomalyDetected anomaly, CancellationToken ct = default)
        {
            Anomalies.Add(anomaly);
            return ValueTask.CompletedTask;
        }
    }

    private static AmplitudeSample Sample(MacAddress node, MacAddress source, WifiChannel channel, double amplitude, int offsetMs)
        => new(new LinkIdentity(node, source, channel), T0 + TimeSpan.FromMilliseconds(offsetMs), amplitude, new Rssi(-55));

    /// <summary>Feeds a stable low-variance stream from every node on the channel.</summary>
    private static void FeedStableStream(SensingOrchestrator orch, WifiChannel channel, MacAddress source, int framesPerNode, int startMs = 0)
    {
        var i = 0;
        foreach (var node in Nodes)
        {
            for (var f = 0; f < framesPerNode; f++, i++)
            {
                orch.OnAmplitudeSampleReceived(Sample(node, source, channel, f % 2 == 0 ? 1.00 : 1.02, startMs + (i * 10)));
            }
        }
    }

    private static (SensingOrchestrator Orch, FakeRadio Radio, FakeSink Sink) Create(
        SensingThresholds? thresholds = null)
    {
        var radio = new FakeRadio();
        var sink = new FakeSink();
        return (new SensingOrchestrator(radio, sink, Nodes, thresholds ?? Fast), radio, sink);
    }

    #region Telemetry Hot Path

    [Fact]
    public void Converged_baseline_spike_returns_and_publishes_anomaly()
    {
        // Arrange — converge all nodes on ch6, then spike.
        var (orch, _, sink) = Create();
        FeedStableStream(orch, Ch6, Target, BaselineTunables.DefaultWindowSize + 1);

        // Act
        var anomaly = orch.OnAmplitudeSampleReceived(Sample(Nodes[0], Target, Ch6, 5.0, 10_000));

        // Assert
        anomaly.Should().NotBeNull();
        anomaly!.Link.Source.Should().Be(Target);
        sink.Anomalies.Should().ContainSingle().Which.Should().Be(anomaly);
    }

    #endregion

    #region Ingestion Gatekeeper & Resync

    [Fact]
    public void Multicast_and_locally_administered_frames_are_dropped()
    {
        // Arrange — a randomized probe-request MAC and a multicast MAC.
        var (orch, _, _) = Create();
        var randomized = MacAddress.Parse("02:11:22:33:44:55");
        var multicast = MacAddress.Parse("01:00:5E:00:00:01");

        // Act
        var a = orch.OnAmplitudeSampleReceived(Sample(Nodes[0], randomized, Ch6, 1.0, 0));
        var b = orch.OnAmplitudeSampleReceived(Sample(Nodes[0], multicast, Ch6, 1.0, 10));

        // Assert — dropped at the door: no baselines, no anomalies.
        a.Should().BeNull();
        b.Should().BeNull();
        orch.TrackedBaselineCount.Should().Be(0);
    }

    [Fact]
    public void Dormant_baselines_are_pruned()
    {
        // Arrange — baselines fed once, then silent for over 10 minutes.
        var (orch, _, _) = Create();
        FeedStableStream(orch, Ch6, Target, 5);
        orch.TrackedBaselineCount.Should().BeGreaterThan(0);

        // Act
        orch.Tick(T0 + TimeSpan.FromMinutes(11));

        // Assert
        orch.TrackedBaselineCount.Should().Be(0);
    }

    [Fact]
    public void Sustained_wrong_channel_frames_trigger_command_reissue()
    {
        // Arrange — locked on ch6; the radio was last told (ch6, filter).
        var radio = new FakeRadio();
        var orch = DriveToAcquiring(radio, new FakeSink(), Fast);
        orch.Campaign.LockedChannel.Should().Be(Ch6);
        int callsBefore = radio.Calls.Count;
        var ch1 = new WifiChannel(1);

        // Act — 8 consecutive frames stamped ch1: the hop was dropped.
        for (var i = 0; i < 8; i++)
        {
            orch.OnAmplitudeSampleReceived(Sample(Nodes[0], Target, ch1, 1.0, 30_000 + (i * 10)));
        }

        // Assert — the lock command was reissued for the believed channel.
        radio.Calls.Count.Should().BeGreaterThan(callsBefore);
        radio.Calls[^1].Channel.Should().Be(Ch6);
        radio.Calls[^1].Filter.Should().NotBeEmpty();
    }

    #endregion

    #region Node Liveness

    [Fact]
    public void Silent_nodes_are_unregistered_after_liveness_timeout()
    {
        // Arrange — all three nodes registered via csi frames during the sweep.
        var (orch, _, _) = Create();
        FeedStableStream(orch, Ch6, Target, 5);
        orch.ExpectedNodeCount.Should().Be(3);

        // Act — nothing heard for over 10 seconds.
        orch.Tick(T0 + TimeSpan.FromSeconds(11));

        // Assert
        orch.ExpectedNodeCount.Should().Be(0);
    }

    [Fact]
    public void Heartbeat_refreshes_node_liveness()
    {
        // Arrange — roster seeded with 3 nodes; only Nodes[0] heartbeats.
        var (orch, _, _) = Create();
        orch.OnNodeHeartbeat(Nodes[0], T0);

        // Act — first tick seeds the never-seen nodes' grace period;
        // Nodes[0] heartbeats again at +12s, inside its window.
        orch.Tick(T0 + TimeSpan.FromSeconds(5));
        orch.OnNodeHeartbeat(Nodes[0], T0 + TimeSpan.FromSeconds(12));
        orch.Tick(T0 + TimeSpan.FromSeconds(16));

        // Assert — grace nodes died 11s after first tick; Nodes[0] lives (4s).
        orch.ExpectedNodeCount.Should().Be(1);

        // Nodes[0] goes silent too — watchdogged 11s after its last heartbeat.
        orch.Tick(T0 + TimeSpan.FromSeconds(23));
        orch.ExpectedNodeCount.Should().Be(0);
    }

    [Fact]
    public void Dead_node_does_not_stall_convergence()
    {
        // Arrange — locked; two nodes stay alive, the third goes silent.
        var orch = DriveToAcquiring(new FakeRadio(), new FakeSink(), Fast);
        var locked = orch.Campaign.LockedChannel!.Value;
        // Feed only the first two nodes so the third's liveness expires.
        foreach (var node in Nodes.Take(2))
        {
            orch.OnAmplitudeSampleReceived(Sample(node, Target, locked, 1.0, 6_000));
        }

        // Act — third node silent >10s -> unregistered; survivors converge.
        orch.Tick(T0 + TimeSpan.FromSeconds(11));
        orch.ExpectedNodeCount.Should().Be(2);
        foreach (var node in Nodes.Take(2))
        {
            for (var f = 0; f < BaselineTunables.DefaultWindowSize + 1; f++)
            {
                orch.OnAmplitudeSampleReceived(Sample(node, Target, locked, f % 2 == 0 ? 1.00 : 1.02, 12_000 + (f * 10)));
            }
        }

        orch.Tick(T0 + TimeSpan.FromSeconds(13));

        // Assert — convergence evaluated against the two live nodes only.
        orch.Campaign.Mode.Should().Be(CampaignMode.Detecting);
    }

    #endregion

    #region Survey Sweep Execution

    [Fact]
    public void Tick_in_surveying_starts_channel_sweep()
    {
        // Arrange
        var (orch, radio, _) = Create();

        // Act
        orch.Tick(T0);

        // Assert — sweep begins on the first plan channel with an open filter.
        radio.Calls.Should().NotBeEmpty();
        radio.Calls[0].Filter.Should().BeEmpty("survey listens to all transmitters");
    }

    [Fact]
    public void Sweep_hops_channels_after_each_dwell()
    {
        // Arrange
        var (orch, radio, _) = Create();
        orch.Tick(T0);
        int initialCalls = radio.Calls.Count;

        // Act — advance past one survey dwell.
        orch.Tick(T0 + Fast.SurveyDwell + TimeSpan.FromMilliseconds(50));

        // Assert — hopped to the next channel.
        radio.Calls.Count.Should().BeGreaterThan(initialCalls);
        radio.Calls[^1].Channel.Should().NotBe(radio.Calls[0].Channel);
    }

    #endregion

    #region Acquisition & Detection

    /// <summary>Drives the campaign through survey into Acquiring: sweeps
    /// {1,6,11}, feeds ch6 activity during its dwell (the hop resets each
    /// channel's activity window, so frames must land inside it), then lets
    /// the post-sweep evaluation issue BeginAcquisition.</summary>
    private static SensingOrchestrator DriveToAcquiring(FakeRadio radio, FakeSink sink, SensingThresholds thresholds)
    {
        var dwell = thresholds.SurveyDwell;
        var orch = new SensingOrchestrator(radio, sink, Nodes, thresholds);
        orch.Tick(T0);                                                   // hop ch1 (plan {1,6,11})
        orch.Tick(T0 + dwell + TimeSpan.FromMilliseconds(50));           // hop ch6 — window resets
        FeedStableStream(orch, Ch6, Target, 20, startMs: 200);           // frames during ch6 dwell
        orch.Tick(T0 + (dwell * 2) + TimeSpan.FromMilliseconds(100));    // hop ch11
        orch.Tick(T0 + (dwell * 3) + TimeSpan.FromMilliseconds(100));    // plan exhausted
        orch.Tick(T0 + (dwell * 3) + TimeSpan.FromMilliseconds(300));    // evaluate -> BeginAcquisition
        return orch;
    }

    /// <summary>Extends <see cref="DriveToAcquiring"/> through convergence into Detecting.</summary>
    private static SensingOrchestrator DriveToDetecting(FakeRadio radio, FakeSink sink, SensingThresholds thresholds)
    {
        var orch = DriveToAcquiring(radio, sink, thresholds);
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);
        var locked = orch.Campaign.LockedChannel!.Value;
        FeedStableStream(orch, locked, Target, BaselineTunables.DefaultWindowSize + 1, startMs: 20_000);
        orch.Tick(T0 + TimeSpan.FromSeconds(21));
        orch.Campaign.Mode.Should().Be(CampaignMode.Detecting);
        return orch;
    }

    [Fact]
    public void Tick_locks_channel_once_survey_finds_active_target()
    {
        // Arrange
        var radio = new FakeRadio();

        // Act
        var orch = DriveToAcquiring(radio, new FakeSink(), Fast);

        // Assert — campaign locked onto ch6 and issued a filtered set_rf.
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);
        orch.Campaign.LockedChannel.Should().Be(Ch6);
        radio.Calls.Should().Contain(c => !c.Filter.IsEmpty);
    }

    [Fact]
    public void Campaign_enters_detecting_when_all_nodes_converge()
    {
        // Act
        var orch = DriveToDetecting(new FakeRadio(), new FakeSink(), Fast);

        // Assert
        orch.Campaign.Mode.Should().Be(CampaignMode.Detecting);
    }

    #endregion

    #region Audit Execution

    [Fact]
    public void Audit_hops_channels_and_returns_to_lock_without_leaving_detecting()
    {
        // Arrange — Detecting; keep confidence high so the audit interval is MaxAuditInterval.
        var radio = new FakeRadio();
        var orch = DriveToDetecting(radio, new FakeSink(), Fast);
        var locked = orch.Campaign.LockedChannel!.Value;
        FeedStableStream(orch, locked, Target, 10, startMs: 24_000);

        // Act — tick past the audit interval, then advance through the audit dwells.
        orch.Tick(T0 + TimeSpan.FromSeconds(27));

        // Assert — hopped to a non-locked channel with an open filter.
        radio.Calls[^1].Channel.Should().NotBe(locked);
        radio.Calls[^1].Filter.Should().BeEmpty();

        // Act — exhaust the audit plan.
        orch.Tick(T0 + TimeSpan.FromSeconds(27.1));
        orch.Tick(T0 + TimeSpan.FromSeconds(27.2));

        // Assert — returned to the locked channel with the campaign filter; never left Detecting.
        radio.Calls[^1].Channel.Should().Be(locked);
        radio.Calls[^1].Filter.Should().NotBeEmpty();
        orch.Campaign.Mode.Should().Be(CampaignMode.Detecting);
    }

    #endregion

    #region Decay & Recovery

    [Fact]
    public void Dead_air_during_acquisition_returns_to_surveying()
    {
        // Arrange — locked, then feed nothing on the channel.
        var orch = DriveToAcquiring(new FakeRadio(), new FakeSink(), Fast);
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);

        // Act — dwell past the dead-channel timeout with zero new frames.
        orch.Tick(T0 + TimeSpan.FromSeconds(5));

        // Assert
        orch.Campaign.Mode.Should().Be(CampaignMode.Surveying);
        orch.DrainEvents().Should().Contain(e => e is ConfidenceDegraded);
    }

    [Fact]
    public void Target_shifted_returns_to_surveying_with_reason()
    {
        // Arrange — Detecting; feed frames from a DIFFERENT mac: channel alive, target gone.
        var orch = DriveToDetecting(new FakeRadio(), new FakeSink(), Fast);
        var locked = orch.Campaign.LockedChannel!.Value;
        foreach (var node in Nodes)
        {
            for (var f = 0; f < 4; f++)
            {
                orch.OnAmplitudeSampleReceived(Sample(node, OtherMac, locked, 1.0, 22_000 + (f * 10)));
            }
        }

        // Act
        orch.Tick(T0 + TimeSpan.FromSeconds(23));

        // Assert — collapsed confidence + live channel = target moved, not dead air.
        orch.Campaign.Mode.Should().Be(CampaignMode.Surveying);
        orch.DrainEvents().OfType<ConfidenceDegraded>().Should().Contain(e => e.Reason == SurveyReason.TargetShifted);
    }

    [Fact]
    public void Reacquire_resets_baselines_without_false_dead_air()
    {
        // Arrange — Detecting; feed a sparse target stream so confidence lands
        // in the reacquire band (pps component ~0.4).
        var orch = DriveToDetecting(new FakeRadio(), new FakeSink(), Fast);
        var locked = orch.Campaign.LockedChannel!.Value;
        for (var f = 0; f < 4; f++)
        {
            orch.OnAmplitudeSampleReceived(Sample(Nodes[0], Target, locked, 1.0, 22_000 + (f * 10)));
        }

        // Act — Reacquire fires and resets the channel's baselines.
        orch.Tick(T0 + TimeSpan.FromSeconds(23));

        // Assert — back in Acquiring.
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);

        // Act — regression: post-reset deltas must not read dead. Feed frames,
        // then tick past the dead-channel timeout.
        FeedStableStream(orch, locked, Target, 4, startMs: 24_000);
        orch.Tick(T0 + TimeSpan.FromSeconds(26));

        // Assert — the reset rebased the delta counters: live channel, no DeadAir skip.
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);
    }

    #endregion
}
