using CsiHub.Ingestion.Models;

namespace CsiHub.Features.Home.Models;

/// <summary>
/// UI projection of a node's current connection and liveness state.
/// </summary>
public sealed class NodeStateViewModel
{
    public string Key { get; set; } = string.Empty;

    public string PortName { get; set; } = string.Empty;

    public string? Mac { get; set; }

    public NodeConnectionState State { get; set; }

    /// <summary>
    /// The device-provided uptime value, in seconds, from the last heartbeat.
    /// </summary>
    public long? Uptime { get; set; }

    /// <summary>
    /// The host-local time the node was last seen (heartbeat received) or the
    /// state change time if no heartbeat data is available.
    /// </summary>
    public DateTimeOffset? LastSeen { get; set; }

    public bool IsDisconnected => State == NodeConnectionState.Disconnected;

    /// <summary>
    /// Live clock_leader flag reported by the node in its heartbeat.
    /// </summary>
    public bool? ClockLeader { get; set; }

    /// <summary>
    /// Live imu_host flag reported by the node in its heartbeat.
    /// </summary>
    public bool? ImuHost { get; set; }

    /// <summary>
    /// Active Wi-Fi bandwidth in MHz (20 or 40) reported by the node.
    /// </summary>
    public int? Bandwidth { get; set; }

    /// <summary>
    /// The persistent hardware configuration for this node, if one exists.
    /// </summary>
    public NodeConfiguration? Configuration { get; set; }

    /// <summary>
    /// Active hardware feature errors keyed by feature name (clock_leader / imu_host).
    /// </summary>
    public Dictionary<string, string> ActiveErrors { get; set; } = new();

    /// <summary>
    /// Latest RF scan results keyed by channel, populated from <c>rf_scan</c> payloads.
    /// </summary>
    public Dictionary<int, RfChannelMetrics> RfScan { get; set; } = new();
}
