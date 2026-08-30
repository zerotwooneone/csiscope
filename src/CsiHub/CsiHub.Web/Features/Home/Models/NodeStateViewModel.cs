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
}
