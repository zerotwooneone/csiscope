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
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _activeErrors = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _unavailableFeatures = new();

    private CancellationTokenSource? _cts;
    private Task? _stateTask;
    private Task? _payloadTask;

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
        _stateTask = Task.Run(() => ProcessStateAsync(_cts.Token), _cts.Token);
        _payloadTask = Task.Run(() => ProcessPayloadsAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task RetryFeatureAsync(NodeStateViewModel node, string feature)
    {
        var key = node.Mac ?? node.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_activeErrors.TryGetValue(key, out var errors))
        {
            errors.TryRemove(feature, out _);
        }

        if (_unavailableFeatures.TryGetValue(key, out var unavailable))
        {
            unavailable.Remove(feature);
        }

        _configurationService.TryGetConfiguration(key, out var configuration);
        if (configuration is null)
        {
            _logger.LogWarning("Cannot retry {Feature} for {Key}: no persisted configuration.", feature, key);
            return;
        }

        _nodes.AddOrUpdate(
            key,
            _ => new NodeStateViewModel
            {
                Key = key,
                PortName = node.PortName,
                Mac = node.Mac,
                State = node.State,
                ActiveErrors = GetSnapshot(key),
            },
            (_, existing) =>
            {
                existing.ActiveErrors = GetSnapshot(key);
                return existing;
            });

        _logger.LogInformation("User requested retry of {Feature} for {Key}.", feature, key);
        await PushSetFeaturesAsync(node.PortName, key, configuration).ConfigureAwait(false);
    }

    private async Task ProcessStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var state in _channel.StateReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = state.Mac ?? state.PortName;

                _configurationService.TryGetConfiguration(state.Mac ?? string.Empty, out var existingConfig);

                if (state.Mac is not null && state.State == NodeConnectionState.Disconnected)
                {
                    ClearErrors(state.Mac);
                }

                _nodes.AddOrUpdate(
                    key,
                    _ => CreateViewModel(state, key, existingConfig),
                    (_, existing) => CreateViewModel(state, key, existingConfig, existing));

                if (state.Mac is not null && state.Bandwidth.HasValue)
                {
                    try
                    {
                        await _configurationService
                            .SetBandwidthAsync(state.Mac, state.Bandwidth.Value, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to persist bandwidth for {Mac}.", state.Mac);
                    }
                }

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

    private async Task ProcessPayloadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _channel.StateStorePayloadReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (payload.Type == "error")
                {
                    HandleErrorPayload(payload);
                    continue;
                }

                if (payload.Type == "boot" && !string.IsNullOrWhiteSpace(payload.Mac))
                {
                    _logger.LogInformation("Node {Mac} reported boot; clearing active errors.", payload.Mac);
                    ClearErrors(payload.Mac);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private void HandleErrorPayload(NodePayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Mac) || payload.Cmd != "set_features")
        {
            return;
        }

        var feature = GetFeatureKey(payload);
        if (feature is null)
        {
            _logger.LogWarning(
                "Received set_features error with unknown feature from {Mac}: reason={Reason}.",
                payload.Mac,
                payload.Reason);
            return;
        }

        var reason = payload.Reason ?? "unknown";

        _activeErrors.AddOrUpdate(
            payload.Mac,
            _ => new ConcurrentDictionary<string, string>(new[] { new KeyValuePair<string, string>(feature, reason) }),
            (_, d) =>
            {
                d[feature] = reason;
                return d;
            });

        _unavailableFeatures.AddOrUpdate(
            payload.Mac,
            _ => new HashSet<string> { feature },
            (_, s) =>
            {
                s.Add(feature);
                return s;
            });

        _nodes.AddOrUpdate(
            payload.Mac,
            _ => new NodeStateViewModel
            {
                Key = payload.Mac,
                PortName = payload.PortName ?? string.Empty,
                Mac = payload.Mac,
                State = NodeConnectionState.Standby,
                ActiveErrors = GetSnapshot(payload.Mac),
            },
            (_, existing) =>
            {
                existing.ActiveErrors = GetSnapshot(payload.Mac);
                return existing;
            });

        _logger.LogWarning(
            "Feature {Feature} failed for {Mac} on {Port}: {Reason}.",
            feature,
            payload.Mac,
            payload.PortName ?? "unknown",
            reason);
    }

    private static string? GetFeatureKey(NodePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Param))
        {
            return payload.Param;
        }

        return payload.Reason switch
        {
            "imu_init_failed" => "imu_host",
            "sync_init_failed" => "clock_leader",
            _ => null
        };
    }

    private NodeStateViewModel CreateViewModel(
        NodeStateChanged state,
        string key,
        NodeConfiguration? configuration,
        NodeStateViewModel? existing = null)
    {
        var mac = state.Mac ?? existing?.Mac;

        if (mac is not null && configuration is not null)
        {
            if (state.ClockLeader.HasValue && state.ClockLeader.Value == configuration.ClockLeader)
            {
                _activeErrors.TryGetValue(mac, out var clockErrors);
                clockErrors?.TryRemove("clock_leader", out _);
                RemoveUnavailable(mac, "clock_leader");
            }

            if (state.ImuHost.HasValue && state.ImuHost.Value == configuration.ImuHost)
            {
                _activeErrors.TryGetValue(mac, out var imuErrors);
                imuErrors?.TryRemove("imu_host", out _);
                RemoveUnavailable(mac, "imu_host");
            }

            if (_activeErrors.TryGetValue(mac, out var errors) && errors.IsEmpty)
            {
                _activeErrors.TryRemove(mac, out _);
            }
        }

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
            Bandwidth = state.Bandwidth ?? configuration?.Bandwidth ?? existing?.Bandwidth,
            Configuration = configuration,
            ActiveErrors = mac is not null ? GetSnapshot(mac) : existing?.ActiveErrors ?? new Dictionary<string, string>(),
        };
    }

    private static Dictionary<string, string> GetSnapshot(ConcurrentDictionary<string, ConcurrentDictionary<string, string>> source, string mac)
    {
        if (!source.TryGetValue(mac, out var errors) || errors is null)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>(errors);
    }

    private Dictionary<string, string> GetSnapshot(string mac)
        => GetSnapshot(_activeErrors, mac);

    private void ClearErrors(string mac)
    {
        _activeErrors.TryRemove(mac, out _);
        _unavailableFeatures.TryRemove(mac, out _);
        _lastRestore.TryRemove(mac, out _);
    }

    private void RemoveUnavailable(string mac, string feature)
    {
        if (_unavailableFeatures.TryGetValue(mac, out var features))
        {
            features.Remove(feature);
        }
    }

    private bool IsUnavailable(string mac, string feature)
        => _unavailableFeatures.TryGetValue(mac, out var features) && features.Contains(feature);

    private async Task TryRestoreFeaturesAsync(
        NodeStateChanged state,
        NodeConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        if (configuration is null || state.Mac is null || !ShouldRestore(state, configuration))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastRestore.TryGetValue(state.Mac, out var last) && now - last < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastRestore.AddOrUpdate(state.Mac, now, (_, _) => now);
        await PushSetFeaturesAsync(state.PortName, state.Mac, configuration).ConfigureAwait(false);
    }

    private async Task PushSetFeaturesAsync(string portName, string mac, NodeConfiguration configuration)
    {
        var json = JsonSerializer.Serialize(new
        {
            cmd = "set_features",
            clock_leader = configuration.ClockLeader,
            imu_host = configuration.ImuHost,
        });

        if (!_portManager.TrySendCommand(portName, json))
        {
            _logger.LogWarning(
                "Failed to push feature flags to {Mac} on {Port}.",
                mac,
                portName);

            _lastRestore.TryRemove(mac, out _);
            return;
        }

        _logger.LogInformation(
            "Pushed feature flags to {Mac} on {Port} (clock_leader={ClockLeader}, imu_host={ImuHost}).",
            mac,
            portName,
            configuration.ClockLeader,
            configuration.ImuHost);

        await Task.CompletedTask;
    }

    private bool ShouldRestore(NodeStateChanged state, NodeConfiguration? configuration)
    {
        if (configuration is null || state.Mac is null)
        {
            return false;
        }

        bool clockMismatch = state.ClockLeader.HasValue && state.ClockLeader.Value != configuration.ClockLeader;
        bool imuMismatch = state.ImuHost.HasValue && state.ImuHost.Value != configuration.ImuHost;

        bool clockUnavailable = IsUnavailable(state.Mac, "clock_leader");
        bool imuUnavailable = IsUnavailable(state.Mac, "imu_host");

        return (clockMismatch && !clockUnavailable) || (imuMismatch && !imuUnavailable);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        await WaitForTaskAsync(_stateTask, cancellationToken).ConfigureAwait(false);
        await WaitForTaskAsync(_payloadTask, cancellationToken).ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
    }

    private static async Task WaitForTaskAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
