using System.Text;
using CsiScope.Domain.Model;
using FluentAssertions;
using Xunit;

namespace CsiScope.Infrastructure.Tests;

public class TelemetryParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json);

    #region CSI Frames

    [Fact]
    public void Csi_frame_parses_link_identity_rssi_and_channel()
    {
        // Arrange — src is the packed big-endian ulong for 08:E9:F6:63:9A:CC.
        var line = Line("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":9654321234567,"ch":6,"seq":7,"rssi":-55,"t":12345,"c":[3,4]}""");

        // Act
        var ok = TelemetryParser.TryParse(line, Now, out var frame);

        // Assert
        ok.Should().BeTrue();
        frame.Kind.Should().Be(TelemetryKind.Csi);
        frame.Sample.Link.Node.Should().Be(MacAddress.Parse("14:C1:9F:2E:53:D0"));
        frame.Sample.Link.Source.Should().Be(MacAddress.FromUInt64(9654321234567UL));
        frame.Sample.Link.Channel.Should().Be(new WifiChannel(6));
        frame.Sample.Rssi.Value.Should().Be(-55);
        frame.Sample.Timestamp.Should().Be(Now);
    }

    [Fact]
    public void Csi_amplitude_is_mean_iq_magnitude()
    {
        // Arrange — pairs (3,4) and (6,8): magnitudes 5 and 10 -> mean 7.5.
        var line = Line("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"ch":6,"rssi":-55,"c":[3,4,6,8]}""");

        // Act
        TelemetryParser.TryParse(line, Now, out var frame).Should().BeTrue();

        // Assert
        frame.Sample.Amplitude.Should().BeApproximately(7.5, 1e-9);
    }

    [Fact]
    public void Csi_negative_iq_values_use_signed_magnitude()
    {
        // Arrange — (-3,-4): magnitude 5.
        var line = Line("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"ch":6,"rssi":-55,"c":[-3,-4]}""");

        // Act
        TelemetryParser.TryParse(line, Now, out var frame).Should().BeTrue();

        // Assert
        frame.Sample.Amplitude.Should().BeApproximately(5.0, 1e-9);
    }

    [Theory]
    [InlineData("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"ch":6,"rssi":-55,"c":[]}""")]           // empty c
    [InlineData("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"ch":6,"rssi":-55}""")]               // missing c
    [InlineData("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","ch":6,"rssi":-55,"c":[3,4]}""")]             // missing src
    [InlineData("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"rssi":-55,"c":[3,4]}""")]            // missing ch
    [InlineData("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"ch":6,"rssi":5,"c":[3,4]}""")]       // invalid rssi
    [InlineData("""{"type":"csi","mac":"14:C1:9F:2E:53:D0","src":1,"ch":6,"rssi":-55,"c":[3,4]""")]      // torn json
    [InlineData("""{"type":"hb","mac":"14:C1:9F:2E:53:D0","uptime":5}""")]                             // heartbeat — skipped
    public void Malformed_or_incomplete_frames_return_false(string json)
    {
        // Act & Assert
        TelemetryParser.TryParse(Line(json), Now, out _).Should().BeFalse();
    }

    #endregion

    #region ACK Frames

    [Fact]
    public void Ack_frame_parses_seq_and_success()
    {
        // Arrange
        var line = Line("""{"type":"ack","cmd":"set_rf","success":true,"seq":7,"state":"streaming"}""");

        // Act
        var ok = TelemetryParser.TryParse(line, Now, out var frame);

        // Assert
        ok.Should().BeTrue();
        frame.Kind.Should().Be(TelemetryKind.Ack);
        frame.Ack.Seq.Should().Be(7);
        frame.Ack.Success.Should().BeTrue();
    }

    [Fact]
    public void Ack_failure_parses_success_false()
    {
        // Arrange
        var line = Line("""{"type":"ack","cmd":"set_rf","success":false,"seq":9,"reason":"timeout"}""");

        // Act
        TelemetryParser.TryParse(line, Now, out var frame).Should().BeTrue();

        // Assert
        frame.Ack.Success.Should().BeFalse();
    }

    #endregion

    #region Config Frames

    [Fact]
    public void Config_frame_parses_announced_node_mac()
    {
        // Arrange
        var line = Line("""{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"version":"0.1.0"}""");

        // Act
        var ok = TelemetryParser.TryParse(line, Now, out var frame);

        // Assert
        ok.Should().BeTrue();
        frame.Kind.Should().Be(TelemetryKind.Config);
        frame.AnnouncedMac.Should().Be(MacAddress.Parse("00:11:22:33:44:55"));
    }

    #endregion
}
