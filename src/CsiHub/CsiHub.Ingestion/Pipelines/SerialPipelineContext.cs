using System.Collections.Concurrent;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Per-port mutable state shared by <see cref="IPayloadHandler"/> implementations.
/// Handlers are stateless singletons; all per-port state lives here.
/// </summary>
public sealed class SerialPipelineContext
{
    private readonly string _portName;
    private readonly CsiIngestionChannel _channel;
    private readonly ILogger _logger;

    public SerialPipelineContext(string portName, CsiIngestionChannel channel, ILogger logger)
    {
        _portName = portName;
        _channel = channel;
        _logger = logger;
    }

    public string PortName => _portName;
    public CsiIngestionChannel Channel => _channel;
    public ILogger Logger => _logger;

    /// <summary>
    /// Pending ACK waiters keyed by command sequence number.
    /// </summary>
    public ConcurrentDictionary<int, TaskCompletionSource<Ack>> PendingAcks { get; } = new();

    /// <summary>
    /// Last MAC address observed on this port.
    /// </summary>
    public string? LastMac { get; set; }

    /// <summary>
    /// Last published connection state for this port.
    /// </summary>
    public NodeConnectionState LastState { get; set; } = NodeConnectionState.Disconnected;

    /// <summary>
    /// Set to true after the first config frame is received.
    /// </summary>
    public bool HasSeenConfig { get; set; }

    /// <summary>
    /// Publishes a state change if the state differs from the last published state,
    /// or when <paramref name="force"/> is true.
    /// </summary>
    public void PublishState(
        NodeConnectionState state,
        long? uptime = null,
        DateTimeOffset? receivedAt = null,
        bool force = false,
        bool? clockLeader = null,
        bool? imuHost = null,
        int? bandwidth = null)
    {
        if (!force && LastState == state)
        {
            return;
        }

        LastState = state;

        var change = new NodeStateChanged(
            _portName,
            LastMac,
            state,
            DateTimeOffset.UtcNow,
            uptime,
            receivedAt,
            clockLeader,
            imuHost,
            bandwidth);

        _channel.TryPublishState(change);
    }

    /// <summary>
    /// Parses a firmware state string and publishes the corresponding state change.
    /// </summary>
    public void PublishStateFromString(
        string? state,
        long? uptime = null,
        DateTimeOffset? receivedAt = null,
        bool force = false,
        bool? clockLeader = null,
        bool? imuHost = null,
        int? bandwidth = null)
    {
        PublishState(
            ParseConnectionState(state),
            uptime,
            receivedAt,
            force,
            clockLeader,
            imuHost,
            bandwidth);
    }

    public static NodeConnectionState ParseConnectionState(string? state)
    {
        var lowered = state?.ToLowerInvariant();

        return lowered switch
        {
            "standby" or "boot" => NodeConnectionState.Standby,
            "streaming" => NodeConnectionState.Streaming,
            "diag_sync" => NodeConnectionState.DiagSync,
            "diag_rf" => NodeConnectionState.DiagRf,
            null => NodeConnectionState.Disconnected,
            _ when lowered.StartsWith("diag_") => NodeConnectionState.Assigned,
            _ => NodeConnectionState.Standby
        };
    }
}
