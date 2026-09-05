using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Development-only fake IMU source. Publishes synthetic quaternion payloads for
/// nodes that have <c>imu_host</c> enabled so the pipeline can be exercised before
/// the BNO085 hardware is wired up.
/// </summary>
public sealed class FakeImuSource : BackgroundService
{
    private readonly CsiNodeStateStore _store;
    private readonly CsiIngestionChannel _channel;
    private readonly ILogger<FakeImuSource> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(100);

    public FakeImuSource(
        CsiNodeStateStore store,
        CsiIngestionChannel channel,
        ILogger<FakeImuSource> logger)
    {
        _store = store;
        _channel = channel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FakeImuSource started; publishing synthetic IMU payloads every {Interval}ms.", _interval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var node in _store.Nodes.Values)
                {
                    if (node.ImuHost == true && !string.IsNullOrEmpty(node.Mac))
                    {
                        var payload = new NodePayload
                        {
                            Type = "imu",
                            Mac = node.Mac,
                            PortName = node.PortName,
                            Imu = new[] { 1.0, 0.0, 0.0, 0.0 },
                            ReceivedAt = DateTimeOffset.UtcNow,
                        };

                        _channel.TryPublish(payload);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FakeImuSource publish failed.");
            }

            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
