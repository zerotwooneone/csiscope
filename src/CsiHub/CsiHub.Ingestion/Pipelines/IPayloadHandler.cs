using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Handles a single class of node payload. Implementations are resolved from DI
/// and invoked by <see cref="PayloadDispatcher"/> in registration order.
/// </summary>
public interface IPayloadHandler
{
    /// <summary>
    /// Returns true when this handler should process the payload.
    /// </summary>
    bool CanHandle(NodePayload payload);

    /// <summary>
    /// Processes the payload. <paramref name="rawSpan"/> is the original UTF-8
    /// JSON bytes for handlers that need raw logging.
    /// </summary>
    void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context);
}
