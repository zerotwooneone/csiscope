using CsiScope.Domain.Baselining;
using CsiScope.Domain.Model;
using FluentAssertions;
using Xunit;

namespace CsiScope.Domain.Tests;

public class LinkBaselineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly LinkIdentity Link = new(
        MacAddress.Parse("14:C1:9F:2E:53:D0"),
        MacAddress.Parse("08:E9:F6:63:9A:CC"),
        new WifiChannel(6));

    private static AmplitudeSample Sample(double amplitude, int offsetMs)
        => new(Link, T0 + TimeSpan.FromMilliseconds(offsetMs), amplitude, -55);

    private static LinkBaseline ConvergedBaseline()
    {
        var baseline = new LinkBaseline(Link);
        // Low-variance stream: alternates 1.00/1.02 -> variance ~1e-4.
        for (var i = 0; i < LinkBaseline.DefaultWindowSize; i++)
        {
            baseline.Observe(Sample(i % 2 == 0 ? 1.00 : 1.02, i * 10));
        }

        return baseline;
    }

    #region Accumulation & Convergence

    [Fact]
    public void Observe_accumulates_frames_and_fill_fraction()
    {
        // Arrange
        var baseline = new LinkBaseline(Link);

        // Act
        for (var i = 0; i < 16; i++)
        {
            baseline.Observe(Sample(1.0, i * 10));
        }

        // Assert
        baseline.TotalFrames.Should().Be(16);
        baseline.FillFraction.Should().BeApproximately(0.25, 1e-6);
    }

    [Fact]
    public void Baseline_converges_once_window_fills_on_stable_stream()
    {
        // Arrange
        var baseline = ConvergedBaseline();

        // Act
        var converged = baseline.IsConverged;

        // Assert — threshold relationship, not the tuned multiplier constant.
        converged.Should().BeTrue();
        baseline.Floor.Value.Should().BeGreaterThan(0);
        baseline.ConvergenceThreshold.Should().BeGreaterThan(baseline.Floor.Value);
    }

    [Fact]
    public void Baseline_does_not_converge_before_window_fills()
    {
        // Arrange
        var baseline = new LinkBaseline(Link);

        // Act
        for (var i = 0; i < LinkBaseline.DefaultWindowSize - 1; i++)
        {
            baseline.Observe(Sample(1.0, i * 10));
        }

        // Assert
        baseline.IsConverged.Should().BeFalse();
    }

    [Fact]
    public void Adaptive_relock_recovers_from_sustained_elevation()
    {
        // Arrange — converged on a quiet floor, then a sustained
        // elevated-variance stream (alternates ±0.5 -> variance ~0.25).
        var baseline = ConvergedBaseline();
        double originalFloor = baseline.Floor.Value;

        // Act
        for (var i = 0; i < 140; i++)
        {
            baseline.Observe(Sample(i % 2 == 0 ? 1.5 : 0.5, 20_000 + (i * 10)));
        }

        // Assert — floor re-locked at the elevated level instead of latching out.
        baseline.Floor.Value.Should().BeGreaterThan(originalFloor);
        baseline.IsConverged.Should().BeTrue();
    }

    #endregion

    #region Tripwire

    [Fact]
    public void Tripwire_fires_on_spike_after_convergence()
    {
        // Arrange
        var baseline = ConvergedBaseline();

        // Act
        var anomaly = baseline.Observe(Sample(5.0, 10_000));

        // Assert — deviation exceeded the floor; ratio magnitude is tunable.
        anomaly.Should().NotBeNull();
        anomaly!.Link.Should().Be(Link);
        anomaly.DeviationRatio.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void Tripwire_respects_per_link_cooldown()
    {
        // Arrange
        var baseline = ConvergedBaseline();
        baseline.Observe(Sample(5.0, 10_000)).Should().NotBeNull();

        // Act & Assert — second spike 500ms later is inside the cooldown;
        // a third after the cooldown fires again.
        baseline.Observe(Sample(5.0, 10_500)).Should().BeNull();
        baseline.Observe(Sample(5.0, 12_000)).Should().NotBeNull();
    }

    #endregion

    #region Hot-Path Contract

    [Fact]
    public void Observe_is_allocation_free_on_hot_path()
    {
        // Arrange — zero-alloc is an explicit architectural requirement.
        var baseline = ConvergedBaseline();
        baseline.Observe(Sample(1.01, 10_000)); // warm up

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            baseline.Observe(Sample(1.01, 11_000 + i));
        }

        // Assert
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    #endregion
}
