using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion.Pipelines.Handlers;

/// <summary>
/// Fallback handler for payload types that are intentionally ignored or unknown.
/// </summary>
public sealed class IgnoredHandler : IPayloadHandler
{
    public bool CanHandle(NodePayload payload) => true;

    public void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
    {
        if (payload.Type == "post")
        {
            context.Logger.LogDebug(
                "Ignoring telemetry frame of type {Type} from {Port}.",
                payload.Type,
                context.PortName);
        }
        else
        {
            context.Logger.LogWarning(
                "Unknown frame type {Type} from {Port}.",
                payload.Type,
                context.PortName);
        }
    }
}
