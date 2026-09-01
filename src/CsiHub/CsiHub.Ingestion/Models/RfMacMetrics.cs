namespace CsiHub.Ingestion.Models;

/// <summary>
/// Metrics for a single transmitter MAC observed during a per-channel RF dwell.
/// </summary>
public sealed class RfMacMetrics
{
    /// <summary>
    /// Transmitter MAC address as reported by the node.
    /// </summary>
    public string? Mac { get; set; }

    /// <summary>
    /// Number of packets observed from this transmitter.
    /// </summary>
    public long Packets { get; set; }

    /// <summary>
    /// Number of packets with a reported RX error from this transmitter.
    /// </summary>
    public long Errors { get; set; }

    /// <summary>
    /// Minimum RSSI from this transmitter, in dBm.
    /// </summary>
    public double RssiMin { get; set; }

    /// <summary>
    /// Maximum RSSI from this transmitter, in dBm.
    /// </summary>
    public double RssiMax { get; set; }

    /// <summary>
    /// Average RSSI from this transmitter, in dBm.
    /// </summary>
    public double RssiAvg { get; set; }

    /// <summary>
    /// Total time, in milliseconds, over which this transmitter's packets were observed.
    /// </summary>
    public int DurationMs { get; set; }
}
