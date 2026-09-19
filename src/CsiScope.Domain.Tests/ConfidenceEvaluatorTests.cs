using CsiScope.Domain.Baselining;
using CsiScope.Domain.Model;
using CsiScope.Domain.Strategy;
using FluentAssertions;
using Xunit;

namespace CsiScope.Domain.Tests;

/// <summary>
/// Table tests for the pure confidence/liveness derivation — constructed
/// baselines and frame deltas in, ConfidenceScore/ChannelLiveness out.
/// </summary>
public class ConfidenceEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private static LinkIdentity LinkFor(int nodeIndex) => new(
        MacAddress.Parse($"14:C1:9F:2E:53:D{nodeIndex}"),
        MacAddress.Parse("08:E9:F6:63:9A:CC"),
        Ch6);

    /// <summary>A baseline fed <paramref name="frames"/> stable samples ending at <paramref name="lastOffsetMs"/>.</summary>
    private static LinkBaseline Baseline(int nodeIndex, int frames, int lastOffsetMs = 0)
    {
        var baseline = new LinkBaseline(LinkFor(nodeIndex));
        for (var i = 0; i < frames; i++)
        {
            baseline.Observe(new AmplitudeSample(
                baseline.Link,
                T0 + TimeSpan.FromMilliseconds(lastOffsetMs - ((frames - 1 - i) * 10)),
                i % 2 == 0 ? 1.00 : 1.02,
                new Rssi(-55)));
        }

        return baseline;
    }

    #region EvaluateConfidence

    [Fact]
    public void Empty_target_baselines_returns_zero_confidence()
    {
        // Act
        var score = ConfidenceEvaluator.EvaluateConfidence(
            Array.Empty<LinkBaseline>(), observedPps: 5, expectedPps: 5, StaleAfter, T0);

        // Assert
        score.Should().Be(ConfidenceScore.Zero);
    }

    [Fact]
    public void Fill_is_the_weakest_links_window_fill()
    {
        // Arrange — one full window, one half-full.
        var baselines = new[]
        {
            Baseline(0, BaselineTunables.DefaultWindowSize),
            Baseline(1, BaselineTunables.DefaultWindowSize / 2),
        };

        // Act
        var score = ConfidenceEvaluator.EvaluateConfidence(baselines, 5, 5, StaleAfter, T0);

        // Assert
        score.Fill.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void Stability_is_the_converged_fraction()
    {
        // Arrange — two converged, one still filling its window.
        var baselines = new[]
        {
            Baseline(0, BaselineTunables.DefaultWindowSize),
            Baseline(1, BaselineTunables.DefaultWindowSize),
            Baseline(2, 10),
        };

        // Act
        var score = ConfidenceEvaluator.EvaluateConfidence(baselines, 5, 5, StaleAfter, T0);

        // Assert
        score.Stability.Should().BeApproximately(2.0 / 3.0, 1e-6);
    }

    [Fact]
    public void TargetPps_clamps_at_one_when_observed_exceeds_expected()
    {
        // Arrange
        var baselines = new[] { Baseline(0, BaselineTunables.DefaultWindowSize) };

        // Act — observed 10 pps against an expectation of 5.
        var score = ConfidenceEvaluator.EvaluateConfidence(baselines, 10, 5, StaleAfter, T0);

        // Assert
        score.TargetPps.Should().Be(1.0);
    }

    [Fact]
    public void Age_decays_linearly_toward_stale_after()
    {
        // Arrange — newest frame landed 5s before the evaluation instant.
        var baselines = new[] { Baseline(0, BaselineTunables.DefaultWindowSize, lastOffsetMs: 0) };
        var now = T0 + TimeSpan.FromSeconds(5);

        // Act
        var score = ConfidenceEvaluator.EvaluateConfidence(baselines, 5, 5, StaleAfter, now);

        // Assert — halfway through the 10s stale window.
        score.Age.Should().BeApproximately(0.5, 1e-6);
    }

    #endregion

    #region EvaluateLiveness

    [Fact]
    public void Liveness_reports_alive_when_frames_arrived()
    {
        // Act
        var liveness = ConfidenceEvaluator.EvaluateLiveness(Ch6, framesDelta: 12, lastFrameAt: T0);

        // Assert
        liveness.IsAlive.Should().BeTrue();
        liveness.Channel.Should().Be(Ch6);
    }

    [Fact]
    public void Liveness_reports_dead_when_no_frames_arrived()
    {
        // Act
        var liveness = ConfidenceEvaluator.EvaluateLiveness(Ch6, framesDelta: 0, lastFrameAt: null);

        // Assert
        liveness.IsAlive.Should().BeFalse();
    }

    #endregion
}
