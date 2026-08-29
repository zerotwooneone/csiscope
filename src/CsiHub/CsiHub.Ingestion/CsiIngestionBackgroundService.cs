using Microsoft.Extensions.Hosting;

namespace CsiHub.Ingestion;

/// <summary>
/// Hosted service that boots the <see cref="CsiNodePortManager"/> and keeps the
/// background task alive until the application is shut down.
/// </summary>
public sealed class CsiIngestionBackgroundService : BackgroundService
{
    private readonly CsiNodePortManager _portManager;

    public CsiIngestionBackgroundService(CsiNodePortManager portManager)
    {
        _portManager = portManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _portManager.StartAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            // Keep the background service alive until the host begins shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the host is stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _portManager.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
