namespace CsiHub.Ingestion.Models;

/// <summary>
/// Per-transmitter metrics from a channel verification diagnostic dwell.
/// </summary>
public sealed class ChanDiagMacMetrics
{
    /// <summary>
    /// Transmitter MAC address as reported by the node ("AA:BB:CC:DD:EE:FF").
    /// </summary>
    public string? Src { get; set; }

    /// <summary>
    /// Number of on-channel packets observed from this transmitter.
    /// </summary>
    public long Packets { get; set; }

    /// <summary>
    /// Average RSSI from this transmitter, in dBm.
    /// </summary>
    public double RssiAvg { get; set; }

    /// <summary>
    /// RSSI variance from this transmitter across the dwell window.
    /// </summary>
    public double RssiVar { get; set; }

    /// <summary>
    /// Variance of the scalar CSI amplitude (sqrt(I^2 + Q^2)) accumulated
    /// across all subcarriers from this transmitter. Low values indicate a
    /// channel that can form a stable amplitude baseline.
    /// </summary>
    public double AmpVar { get; set; }
}
