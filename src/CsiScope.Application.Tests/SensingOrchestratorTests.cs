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

    private sealed class FakeRadio : IRadioCommandPort
    {
        public List<(WifiChannel Channel, ImmutableArray<MacAddress> Filter)> Calls { get; } = new();

        public Task BroadcastSetRfAsync(WifiChannel channel, ImmutableArray<MacAddress> macFilter, CancellationToken ct = default)
        {
            Calls.Add((channel, macFilter));
            return Task.CompletedTask;
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

    private static (SensingOrchestrator Orch, FakeRadio Radio, FakeSink Sink) Create()
    {
        var radio = new FakeRadio();
        var sink = new FakeSink();
        return (new SensingOrchestrator(radio, sink, Nodes), radio, sink);
    }

    #region Telemetry Hot Path

    [Fact]
    public void Sample_creates_baseline_and_returns_no_anomaly_before_convergence()
    {
        // Arrange
        var (orch, _, _) = Create();

        // Act
        var anomaly = orch.OnAmplitudeSampleReceived(Sample(Nodes[0], Target, Ch6, 1.0, 0));

        // Assert
        anomaly.Should().BeNull();
    }

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

        // Act — advance past one survey dwell (500ms default).
        orch.Tick(T0 + TimeSpan.FromMilliseconds(600));

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
    private static SensingOrchestrator DriveToAcquiring(FakeRadio radio, FakeSink sink)
    {
        var orch = new SensingOrchestrator(radio, sink, Nodes);
        orch.Tick(T0);                                    // hop ch1 (plan {1,6,11})
        orch.Tick(T0 + TimeSpan.FromMilliseconds(600));   // hop ch6 — window resets
        FeedStableStream(orch, Ch6, Target, 20, startMs: 650); // frames during ch6 dwell
        orch.Tick(T0 + TimeSpan.FromMilliseconds(1200));  // hop ch11
        orch.Tick(T0 + TimeSpan.FromMilliseconds(1800));  // plan exhausted
        orch.Tick(T0 + TimeSpan.FromMilliseconds(2400));  // evaluate -> BeginAcquisition
        return orch;
    }

    [Fact]
    public void Tick_locks_channel_once_survey_finds_active_target()
    {
        // Arrange
        var radio = new FakeRadio();

        // Act
        var orch = DriveToAcquiring(radio, new FakeSink());

        // Assert — campaign locked onto ch6 and issued a filtered set_rf.
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);
        orch.Campaign.LockedChannel.Should().Be(Ch6);
        radio.Calls.Should().Contain(c => !c.Filter.IsEmpty);
    }

    [Fact]
    public void Campaign_enters_detecting_when_all_nodes_converge()
    {
        // Arrange
        var orch = DriveToAcquiring(new FakeRadio(), new FakeSink());
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);
        var locked = orch.Campaign.LockedChannel!.Value;

        // Act — fill every node's window on the locked channel, then tick.
        FeedStableStream(orch, locked, Target, BaselineTunables.DefaultWindowSize + 1, startMs: 20_000);
        orch.Tick(T0 + TimeSpan.FromSeconds(21));

        // Assert
        orch.Campaign.Mode.Should().Be(CampaignMode.Detecting);
    }

    #endregion

    #region Decay & Recovery

    [Fact]
    public void Dead_air_during_acquisition_returns_to_surveying()
    {
        // Arrange — locked at +2.4s, then feed nothing on the channel.
        var orch = DriveToAcquiring(new FakeRadio(), new FakeSink());
        orch.Campaign.Mode.Should().Be(CampaignMode.Acquiring);

        // Act — dwell past the 8s dead-channel timeout with zero new frames.
        orch.Tick(T0 + TimeSpan.FromSeconds(12));

        // Assert
        orch.Campaign.Mode.Should().Be(CampaignMode.Surveying);
        orch.DrainEvents().Should().Contain(e => e is ConfidenceDegraded);
    }

    #endregion
}
