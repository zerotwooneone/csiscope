namespace CsiHub.Ingestion.Models;

/// <summary>
/// A lightweight state change event published when a node's serial connection
/// or reported firmware state changes.
/// </summary>
public sealed record NodeStateChanged(
    string PortName,
    string? Mac,
    NodeConnectionState State,
    DateTimeOffset Timestamp);
