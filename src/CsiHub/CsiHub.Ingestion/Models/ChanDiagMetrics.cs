namespace CsiHub.Ingestion.Models;

/// <summary>
/// Channel verification diagnostic emitted by a node after a STATE_DIAG_CHAN
/// dwell: per-transmitter packet, RSSI and CSI amplitude-variance statistics
/// used to rate a channel before establishing persistent baselines.
/// </summary>
public sealed class ChanDiagMetrics
{
    /// <summary>
    /// The 2.4 GHz Wi-Fi channel that was verified (1-13).
    /// </summary>
    public int Channel { get; set; }

    /// <summary>
    /// Length of the dwell window, in milliseconds.
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Total on-channel packets observed during the dwell window.
    /// </summary>
    public long TotalPackets { get; set; }

    /// <summary>
    /// Per-transmitter statistics collected during the dwell, sorted by
    /// packet count descending.
    /// </summary>
    public List<ChanDiagMacMetrics>? Macs { get; set; }
}
