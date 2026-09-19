using System.Collections.Immutable;
using CsiScope.Domain.Model;
using FluentAssertions;
using Xunit;

namespace CsiScope.Domain.Tests;

public class MacAddressTests
{
    [Theory]
    [InlineData("08:E9:F6:63:9A:CC")]
    [InlineData("08E9F6639ACC")]
    [InlineData("08-e9-f6-63-9a-cc")]
    [InlineData("08e9f6639acc")]
    public void Parses_all_supported_formats(string text)
    {
        // Act
        var mac = MacAddress.Parse(text);

        // Assert
        mac.ToCanonicalString().Should().Be("08E9F6639ACC");
        mac.ToString().Should().Be("08:E9:F6:63:9A:CC");
    }

    [Theory]
    [InlineData("08:E9:F6:63:9A")]       // too short
    [InlineData("08:E9:F6:63:9A:CC:DD")] // too long
    [InlineData("GG:E9:F6:63:9A:CC")]    // non-hex
    [InlineData("")]
    public void Rejects_invalid_input(string text)
    {
        // Act & Assert
        MacAddress.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Classifies_multicast_and_locally_administered()
    {
        // Act & Assert
        MacAddress.Parse("01:00:5E:00:00:01").IsMulticast.Should().BeTrue();
        MacAddress.Parse("02:00:00:00:00:01").IsLocallyAdministered.Should().BeTrue();
        MacAddress.Parse("08:E9:F6:63:9A:CC").IsMulticast.Should().BeFalse();
        MacAddress.Parse("08:E9:F6:63:9A:CC").IsLocallyAdministered.Should().BeFalse();
    }

    [Fact]
    public void Equality_is_value_based_across_input_formats()
    {
        // Act & Assert — regression guard: colon and canonical forms must
        // compare equal (the production lookup bug this prevents).
        MacAddress.Parse("08:E9:F6:63:9A:CC")
            .Should().Be(MacAddress.Parse("08e9f6639acc"));
    }
}

public class WifiChannelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(-1)]
    public void Rejects_out_of_range_channels(int value)
    {
        // Act
        var act = () => new WifiChannel(value);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(11, true)]
    [InlineData(7, false)]
    [InlineData(13, false)]
    public void Identifies_non_overlapping_channels(int value, bool expected)
    {
        // Act & Assert
        new WifiChannel(value).IsNonOverlapping.Should().Be(expected);
    }
}

public class ConfidenceScoreTests
{
    [Fact]
    public void Components_clamp_to_unit_interval()
    {
        // Act
        var score = new ConfidenceScore(1.5, -0.2, 0.5, 2.0);

        // Assert
        score.Fill.Should().Be(1.0);
        score.Stability.Should().Be(0.0);
        score.TargetPps.Should().Be(0.5);
        score.Age.Should().Be(1.0);
    }

    [Fact]
    public void Composite_collapses_when_any_component_is_zero()
    {
        // Act & Assert
        new ConfidenceScore(1, 1, 1, 0).Value.Should().Be(0);
        ConfidenceScore.Full.Value.Should().Be(1);
    }
}

public class RssiTests
{
    [Theory]
    [InlineData((short)1)]      // positive dBm is not a real measurement
    [InlineData((short)-200)]   // below physical floor
    public void Rejects_out_of_range_values(short value)
    {
        // Act
        var act = () => new Rssi(value);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Accepts_typical_dbm_reading()
    {
        // Act & Assert
        new Rssi(-55).Value.Should().Be(-55);
    }
}

public class ActivityScoreTests
{
    [Fact]
    public void Rejects_negative_and_nan()
    {
        // Act & Assert
        FluentActions.Invoking(() => new ActivityScore(-1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ActivityScore(double.NaN)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Orders_by_value_for_candidate_ranking()
    {
        // Act & Assert
        new ActivityScore(9.0).Should().BeGreaterThan(new ActivityScore(3.0));
    }
}

public class DeviationRatioTests
{
    [Fact]
    public void Rejects_negative_and_nan()
    {
        // Act & Assert
        FluentActions.Invoking(() => new DeviationRatio(-0.5)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new DeviationRatio(double.NaN)).Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class SensingDecisionTests
{
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");

    [Fact]
    public void BeginAcquisition_with_equal_filter_contents_compares_equal()
    {
        // Arrange — different array instances, identical contents.
        var a = new SensingDecision.BeginAcquisition(new WifiChannel(7), ImmutableArray.Create(Target));
        var b = new SensingDecision.BeginAcquisition(new WifiChannel(7), ImmutableArray.Create(Target));

        // Act & Assert — regression guard: record equality on collections
        // must be sequence-based, not reference-based.
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void BeginAcquisition_with_different_filter_compares_unequal()
    {
        // Arrange
        var a = new SensingDecision.BeginAcquisition(new WifiChannel(7), ImmutableArray.Create(Target));
        var b = new SensingDecision.BeginAcquisition(new WifiChannel(7),
            ImmutableArray.Create(Target, MacAddress.Parse("14:C1:9F:2E:53:D0")));

        // Act & Assert
        a.Should().NotBe(b);
    }

    [Fact]
    public void ScanPlan_with_equal_channels_compares_equal()
    {
        // Arrange
        var a = new ScanPlan(ImmutableArray.Create(new WifiChannel(6), new WifiChannel(11)), TimeSpan.FromMilliseconds(250));
        var b = new ScanPlan(ImmutableArray.Create(new WifiChannel(6), new WifiChannel(11)), TimeSpan.FromMilliseconds(250));

        // Act & Assert
        a.Should().Be(b);
    }

    [Fact]
    public void AuditChannels_equality_flows_through_scan_plan()
    {
        // Arrange
        var plan = new ScanPlan(ImmutableArray.Create(new WifiChannel(7)), TimeSpan.FromMilliseconds(250));
        var a = new SensingDecision.AuditChannels(plan, new WifiChannel(6));
        var b = new SensingDecision.AuditChannels(
            new ScanPlan(ImmutableArray.Create(new WifiChannel(7)), TimeSpan.FromMilliseconds(250)), new WifiChannel(6));

        // Act & Assert
        a.Should().Be(b);
    }
}
