using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.Pipelines.Handlers;

/// <summary>
/// Handles heartbeat frames that carry node state, uptime, and feature flags.
/// </summary>
public sealed class HeartbeatHandler : IPayloadHandler
{
    public bool CanHandle(NodePayload payload) => payload.Type == "hb";

    public void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
    {
        if (!string.IsNullOrEmpty(payload.Mac))
        {
            context.LastMac = payload.Mac;
        }

        if (string.IsNullOrEmpty(payload.State))
        {
            return;
        }

        context.PublishStateFromString(
            payload.State,
            payload.Timestamp,
            payload.ReceivedAt,
            force: true,
            payload.ClockLeader,
            payload.ImuHost,
            payload.Bandwidth);
    }
}
