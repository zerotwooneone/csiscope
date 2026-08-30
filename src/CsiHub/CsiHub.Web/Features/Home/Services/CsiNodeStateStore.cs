using System.Collections.Concurrent;
using System.Text.Json;
using CsiHub.Features.Home.Models;
using CsiHub.Ingestion;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Singleton state projection for the low-level engineering dashboard.
/// Drains <see cref="CsiIngestionChannel.StateReader"/> and keeps a thread-safe
/// cache of the latest known state per node, keyed by MAC address (or COM port
/// when the MAC is not yet known).
/// </summary>
public sealed class CsiNodeStateStore : IHostedService, IAsyncDisposable
{
    private readonly CsiIngestionChannel _channel;
    private readonly CsiNodePortManager _portManager;
    private readonly CsiNodeConfigurationService _configurationService;
    private readonly ILogger<CsiNodeStateStore> _logger;
    private readonly ConcurrentDictionary<string, NodeStateViewModel> _nodes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRestore = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public CsiNodeStateStore(
        CsiIngestionChannel channel,
        CsiNodePortManager portManager,
        CsiNodeConfigurationService configurationService,
        ILogger<CsiNodeStateStore> logger)
    {
        _channel = channel;
        _portManager = portManager;
        _configurationService = configurationService;
        _logger = logger;
    }

    /// <summary>
    /// The current node snapshot. This is safe to read from Blazor components.
    /// </summary>
    public IReadOnlyDictionary<string, NodeStateViewModel> Nodes => _nodes;

    /// <summary>
    /// Attempts to look up the COM port associated with a node, using its
    /// MAC address or fallback key. Returns true when the node is known.
    /// </summary>
    public bool TryGetPortName(string mac, out string? portName)
    {
        if (_nodes.TryGetValue(mac, out var node))
        {
            portName = node.PortName;
            return true;
        }

        portName = null;
        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var state in _channel.StateReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = state.Mac ?? state.PortName;

                _configurationService.TryGetConfiguration(state.Mac ?? string.Empty, out var existingConfig);

                _nodes.AddOrUpdate(
                    key,
                    _ => CreateViewModel(state, key, existingConfig),
                    (_, existing) => CreateViewModel(state, key, existingConfig, existing));

                if (state.Mac is not null && ShouldRestore(state, existingConfig))
                {
                    await TryRestoreFeaturesAsync(state, existingConfig, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private NodeStateViewModel CreateViewModel(
        NodeStateChanged state,
        string key,
        NodeConfiguration? configuration,
        NodeStateViewModel? existing = null)
    {
        var mac = state.Mac ?? existing?.Mac;

        return new NodeStateViewModel
        {
            Key = key,
            PortName = state.PortName,
            Mac = mac,
            State = state.State,
            Uptime = state.Uptime ?? existing?.Uptime,
            LastSeen = state.ReceivedAt ?? state.Timestamp,
            ClockLeader = state.ClockLeader,
            ImuHost = state.ImuHost,
            Configuration = configuration,
        };
    }

    private async Task TryRestoreFeaturesAsync(
        NodeStateChanged state,
        NodeConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        if (configuration is null || state.Mac is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastRestore.TryGetValue(state.Mac, out var last) && now - last < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastRestore.AddOrUpdate(state.Mac, now, (_, _) => now);

        var json = JsonSerializer.Serialize(new
        {
            cmd = "set_features",
            clock_leader = configuration.ClockLeader,
            imu_host = configuration.ImuHost,
        });

        if (!_portManager.TrySendCommand(state.PortName, json))
        {
            _logger.LogWarning(
                "Failed to auto-restore feature flags to {Mac} on {Port}.",
                state.Mac,
                state.PortName);

            _lastRestore.TryRemove(state.Mac, out _);
            return;
        }

        _logger.LogInformation(
            "Auto-restored feature flags to {Mac} on {Port} (clock_leader={ClockLeader}, imu_host={ImuHost}).",
            state.Mac,
            state.PortName,
            configuration.ClockLeader,
            configuration.ImuHost);
    }

    private static bool ShouldRestore(NodeStateChanged state, NodeConfiguration? configuration)
    {
        if (configuration is null || state.Mac is null)
        {
            return false;
        }

        // If the heartbeat reports feature flags, correct any drift from the persisted config.
        if (state.ClockLeader.HasValue && state.ClockLeader.Value != configuration.ClockLeader)
        {
            return true;
        }

        if (state.ImuHost.HasValue && state.ImuHost.Value != configuration.ImuHost)
        {
            return true;
        }

        return false;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
