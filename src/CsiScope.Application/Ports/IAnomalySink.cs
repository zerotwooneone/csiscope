using CsiScope.Domain.Events;

namespace CsiScope.Application.Ports;

/// <summary>
/// Outbound port for tripwire events. The adapter decides delivery
/// (in-memory bus, SignalR, log) — the orchestrator only publishes.
/// </summary>
public interface IAnomalySink
{
    ValueTask PublishAsync(AnomalyDetected anomaly, CancellationToken ct = default);
}
