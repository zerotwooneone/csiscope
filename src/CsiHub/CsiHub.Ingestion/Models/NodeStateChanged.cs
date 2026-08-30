namespace CsiHub.Ingestion.Models;

/// <summary>
/// A lightweight state change event published when a node's serial connection
/// or reported firmware state changes. Carries the latest heartbeat's hardware
/// uptime and the host-local receive time for liveness tracking.
/// </summary>
public sealed record NodeStateChanged(
    string PortName,
    string? Mac,
    NodeConnectionState State,
    DateTimeOffset Timestamp,
    long? Uptime = null,
    DateTimeOffset? ReceivedAt = null,
    bool? ClockLeader = null,
    bool? ImuHost = null,
    int? Bandwidth = null);
