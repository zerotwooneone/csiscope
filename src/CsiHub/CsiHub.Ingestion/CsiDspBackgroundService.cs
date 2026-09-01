using System.Collections.Concurrent;
using CsiHub.Core;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion;

/// <summary>
/// Hosted service that consumes the DSP payload channel and maintains per-node
/// <see cref="RoomBaseline"/> statistics. This is the hook point for raw individual
/// node processing before any cross-node fusion (such as the follower/leader CSI ratio).
/// </summary>
public sealed class CsiDspBackgroundService : IHostedService, IAsyncDisposable
{
    private readonly CsiIngestionChannel _channel;
    private readonly ILogger<CsiDspBackgroundService> _logger;
    private readonly ConcurrentDictionary<string, RoomBaseline> _baselines = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUpdateAt = new();

    private CancellationTokenSource? _cts;
    private Task? _task;

    public CsiDspBackgroundService(CsiIngestionChannel channel, ILogger<CsiDspBackgroundService> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => ProcessDspAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_task is not null)
        {
            try
            {
                await _task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("DSP pipeline did not stop within the timeout.");
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Snapshot of the current baselines for inspection or downstream fusion.
    /// </summary>
    public IReadOnlyDictionary<string, RoomBaseline> Baselines => _baselines;

    private async Task ProcessDspAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _channel.DspPayloadReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(payload.Mac))
                {
                    continue;
                }

                if (payload.Bandwidth.HasValue)
                {
                    var baseline = _baselines.GetOrAdd(
                        payload.Mac,
                        _ => new RoomBaseline { Mac = payload.Mac! });

                    if (baseline.Bandwidth != payload.Bandwidth.Value)
                    {
                        baseline.Initialize(payload.Bandwidth.Value, RoomBaseline.DefaultWindowSize);
                    }
                }

                if (payload.Type == "csi" && payload.Csi is not null)
                {
                    var csiBaseline = _baselines.GetOrAdd(
                        payload.Mac,
                        _ => new RoomBaseline { Mac = payload.Mac! });

                    if (!csiBaseline.IsInitialized)
                    {
                        if (payload.Bandwidth.HasValue)
                        {
                            csiBaseline.Initialize(payload.Bandwidth.Value, RoomBaseline.DefaultWindowSize);
                        }
                        else
                        {
                            csiBaseline.InitializeFromLength(payload.Csi.Length, RoomBaseline.DefaultWindowSize);
                        }
                    }
                    else if (payload.Bandwidth.HasValue && csiBaseline.Bandwidth != payload.Bandwidth.Value)
                    {
                        csiBaseline.Initialize(payload.Bandwidth.Value, RoomBaseline.DefaultWindowSize);
                    }

                    TimeSpan? dt = null;
                    if (_lastUpdateAt.TryGetValue(payload.Mac!, out var last))
                    {
                        dt = payload.ReceivedAt - last;
                    }

                    csiBaseline.Update(payload.Csi, dt);
                    _lastUpdateAt[payload.Mac!] = payload.ReceivedAt;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
