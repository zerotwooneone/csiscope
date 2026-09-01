namespace CsiHub.Features.Home.Models;

/// <summary>
/// Aggregated RF scan results for a single channel, merged from all participating nodes.
/// </summary>
public sealed class RfChannelAggregate
{
    public int Channel { get; set; }

    /// <summary>
    /// Minimum RSSI observed across all contributing samples, in dBm.
    /// </summary>
    public double RssiMin { get; set; }

    /// <summary>
    /// Maximum RSSI observed across all contributing samples, in dBm.
    /// </summary>
    public double RssiMax { get; set; }

    /// <summary>
    /// Packet-count-weighted average RSSI across all contributing samples, in dBm.
    /// </summary>
    public double RssiAvg { get; set; }

    /// <summary>
    /// Total number of packets observed across all contributing samples.
    /// </summary>
    public long Packets { get; set; }

    /// <summary>
    /// Total number of packets with a reported RX error across all contributing samples.
    /// </summary>
    public long Errors { get; set; }

    /// <summary>
    /// Longest dwell time observed across all contributing samples, in milliseconds.
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Number of nodes that contributed to this aggregate.
    /// </summary>
    public int SampleCount { get; set; }
}
