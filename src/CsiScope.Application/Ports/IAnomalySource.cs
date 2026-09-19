using CsiScope.Domain.Events;

namespace CsiScope.Application.Ports;

/// <summary>
/// Read side of the anomaly channel — consumed by UI/diagnostic pollers.
/// Split from <see cref="IAnomalySink"/> so consumers never see the write API.
/// </summary>
public interface IAnomalySource
{
    /// <summary>Streams anomalies until cancellation; backs off when idle.</summary>
    IAsyncEnumerable<AnomalyDetected> ReadAllAsync(CancellationToken ct = default);
}
