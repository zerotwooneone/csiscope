namespace CsiHub.Ingestion.Models;

/// <summary>
/// Per-channel RF scan metrics emitted by the ESP32-S3 promiscuous diagnostic sweep.
/// </summary>
public sealed class RfChannelMetrics
{
    /// <summary>
    /// The 2.4 GHz Wi-Fi channel that was sampled (1-13).
    /// </summary>
    public int Channel { get; set; }

    /// <summary>
    /// Minimum RSSI observed during the dwell window, in dBm.
    /// </summary>
    public double RssiMin { get; set; }

    /// <summary>
    /// Maximum RSSI observed during the dwell window, in dBm.
    /// </summary>
    public double RssiMax { get; set; }

    /// <summary>
    /// Average RSSI observed during the dwell window, in dBm.
    /// </summary>
    public double RssiAvg { get; set; }

    /// <summary>
    /// Number of packets received during the dwell window.
    /// </summary>
    public long Packets { get; set; }

    /// <summary>
    /// Number of packets with a reported RX error during the dwell window.
    /// </summary>
    public long Errors { get; set; }

    /// <summary>
    /// Length of the dwell window, in milliseconds.
    /// </summary>
    public int DurationMs { get; set; }
}
