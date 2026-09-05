using System.Text.Json.Serialization;

namespace CsiHub.Ingestion.Models;

/// <summary>
/// A parsed NDJSON payload from a CsiScope node. Supports both the compact flat-array
/// keys the firmware emits ("c", "i", "m", "t") and longer debug keys.
/// </summary>
[JsonConverter(typeof(Serialization.NodePayloadJsonConverter))]
public sealed class NodePayload
{
    public string? Type { get; set; }

    public string? Mac { get; set; }

    public long? Timestamp { get; set; }

    public string? State { get; set; }

    public string? Cmd { get; set; }

    /// <summary>
    /// The failing feature or command parameter, e.g. "imu_host".
    /// </summary>
    public string? Param { get; set; }

    public string? Reason { get; set; }

    /// <summary>
    /// Diagnostic test type, present when <see cref="Type"/> is "diag".
    /// </summary>
    public string? Test { get; set; }

    /// <summary>
    /// Command success flag, present when <see cref="Type"/> is "ack".
    /// </summary>
    public bool? Success { get; set; }

    public bool? ClockLeader { get; set; }

    public bool? ImuHost { get; set; }

    /// <summary>
    /// Active Wi-Fi bandwidth in MHz (20 or 40).
    /// </summary>
    public int? Bandwidth { get; set; }

    public double[]? Csi { get; set; }

    /// <summary>
    /// Monotonic sequence number of the CSI payload (per node).
    /// </summary>
    public int? Seq { get; set; }

    /// <summary>
    /// RSSI of the captured CSI frame in dBm.
    /// </summary>
    public int? Rssi { get; set; }

    /// <summary>
    /// Packed source/transmitter MAC address (48 bits stored in a ulong).
    /// </summary>
    public ulong? SrcMac { get; set; }

    public double[]? Imu { get; set; }

    /// <summary>
    /// RF scan metrics for a single channel, present when <see cref="Type"/> is "rf_scan".
    /// </summary>
    public RfChannelMetrics? Rf { get; set; }

    /// <summary>
    /// Sync diagnostic metrics, present when <see cref="Type"/> is "diag" and the test is "sync".
    /// </summary>
    public SyncDiagnosticMetrics? SyncDiag { get; set; }

    /// <summary>
    /// The host-local time the payload was received.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// The COM port this payload was ingested from.
    /// </summary>
    public string? PortName { get; set; }
}
