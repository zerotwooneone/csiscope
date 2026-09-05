using System.Text;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion.Pipelines.Handlers;

/// <summary>
/// Handles the initial config frame that a node sends after connecting.
/// </summary>
public sealed class ConfigHandler : IPayloadHandler
{
    public bool CanHandle(NodePayload payload) => payload.Type == "config";

    public void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
    {
        context.HasSeenConfig = true;

        if (!string.IsNullOrEmpty(payload.Mac))
        {
            context.LastMac = payload.Mac;
        }

        context.Logger.LogInformation(
            "Node {Port} config: {Config}",
            context.PortName,
            Encoding.UTF8.GetString(rawSpan));

        if (!string.IsNullOrEmpty(payload.State))
        {
            context.PublishStateFromString(payload.State, receivedAt: payload.ReceivedAt);
        }
    }
}
