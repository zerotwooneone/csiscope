using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.Pipelines.Handlers;

/// <summary>
/// Handles telemetry payloads that are published to the ingestion channel for
/// downstream consumers (state store and DSP).
/// </summary>
public sealed class TelemetryHandler : IPayloadHandler
{
    private static readonly HashSet<string> TelemetryTypes = new(StringComparer.Ordinal)
    {
        "csi",
        "diag",
        "error",
        "rf_scan",
        "imu",
        "boot",
    };

    public bool CanHandle(NodePayload payload)
        => payload.Type is not null && TelemetryTypes.Contains(payload.Type);

    public void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
    {
        context.Channel.TryPublish(payload);

        if (!string.IsNullOrEmpty(payload.State))
        {
            context.PublishStateFromString(
                payload.State,
                payload.Timestamp,
                payload.ReceivedAt,
                force: payload.Type == "hb",
                payload.ClockLeader,
                payload.ImuHost,
                payload.Bandwidth);
        }
    }
}
