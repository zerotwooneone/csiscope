namespace CsiHub.Ingestion.Models;

/// <summary>
/// Represents the high-level connection and operational state of a single array node.
/// </summary>
public enum NodeConnectionState
{
    Disconnected,
    Standby,
    Assigned,
    Streaming
}
