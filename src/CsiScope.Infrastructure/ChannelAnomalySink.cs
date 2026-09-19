using System.Threading.Channels;
using CsiScope.Application.Ports;
using CsiScope.Domain.Events;

namespace CsiScope.Infrastructure;

/// <summary>
/// <see cref="IAnomalySink"/> backed by a bounded channel — mirrors the
/// legacy pattern of a bounded queue the UI polls. Drop-oldest so a stalled
/// consumer never backs pressure into the sensing path.
/// </summary>
public sealed class ChannelAnomalySink : IAnomalySink
{
    private readonly Channel<AnomalyDetected> _channel = Channel.CreateBounded<AnomalyDetected>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
        });

    /// <summary>UI/poller consumption side.</summary>
    public ChannelReader<AnomalyDetected> Reader => _channel.Reader;

    public ValueTask PublishAsync(AnomalyDetected anomaly, CancellationToken ct = default)
    {
        _channel.Writer.TryWrite(anomaly);
        return ValueTask.CompletedTask;
    }
}
