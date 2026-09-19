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
        score.WindowFill.Should().Be(1.0);
        score.FloorStability.Should().Be(0.0);
        score.IngestionRate.Should().Be(0.5);
        score.Freshness.Should().Be(1.0);
    }

    [Fact]
    public void Composite_collapses_when_any_component_is_zero()
    {
        // Act & Assert
        new ConfidenceScore(1, 1, 1, 0).Value.Should().Be(0);
        ConfidenceScore.Full.Value.Should().Be(1);
    }
}
