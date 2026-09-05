namespace CsiHub.Ingestion.Models;

/// <summary>
/// GPIO sync diagnostic metrics emitted by a node while in STATE_DIAG_SYNC.
/// </summary>
public sealed class SyncDiagnosticMetrics
{
    /// <summary>
    /// Number of trigger pulses counted during the sync test.
    /// </summary>
    public long PulseCount { get; set; }

    /// <summary>
    /// Time from trigger edge to ISR execution, in microseconds.
    /// </summary>
    public double LatencyUs { get; set; }

    /// <summary>
    /// Jitter (variability) of the ISR latency, in microseconds.
    /// </summary>
    public double JitterUs { get; set; }
}
