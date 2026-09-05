using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Routes a parsed <see cref="NodePayload"/> to the first registered
/// <see cref="IPayloadHandler"/> that can handle it.
/// </summary>
public sealed class PayloadDispatcher
{
    private readonly IReadOnlyList<IPayloadHandler> _handlers;

    public PayloadDispatcher(IEnumerable<IPayloadHandler> handlers)
    {
        _handlers = handlers.ToList();
    }

    public void Dispatch(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(payload))
            {
                handler.Handle(payload, rawSpan, context);
                return;
            }
        }
    }
}
