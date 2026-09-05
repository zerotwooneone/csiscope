using System.Collections.Concurrent;
using System.Text.Json;
using CsiHub.Features.Home.Models;
using CsiHub.Ingestion;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using CsiHub.Ingestion.Pipelines;
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
    private readonly RfChannelEvaluator _evaluator;
    private readonly ILogger<CsiNodeStateStore> _logger;
    private readonly ConcurrentDictionary<string, NodeStateViewModel> _nodes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRestore = new();
    private readonly ConcurrentDictionary<string, byte> _featuresRestored = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _activeErrors = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _unavailableFeatures = new();
    private readonly ConcurrentDictionary<string, Dictionary<int, RfChannelMetrics>> _rfScanResults = new();
    private readonly ConcurrentDictionary<int, RfChannelAggregate> _combinedRfScan = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _sweepAssignments = new();
    private readonly ConcurrentDictionary<string, int> _sweepAwaiting = new();
    private TaskCompletionSource? _sweepTcs;
    private int _sweepDwellMs = 250;

    private CancellationTokenSource? _cts;
    private Task? _stateTask;
    private Task? _payloadTask;

    public CsiNodeStateStore(
        CsiIngestionChannel channel,
        CsiNodePortManager portManager,
        CsiNodeConfigurationService configurationService,
        RfChannelEvaluator evaluator,
        ILogger<CsiNodeStateStore> logger)
    {
        _channel = channel;
        _portManager = portManager;
        _configurationService = configurationService;
        _evaluator = evaluator;
        _logger = logger;
    }

    /// <summary>
    /// The current node snapshot. This is safe to read from Blazor components.
    /// </summary>
    public IReadOnlyDictionary<string, NodeStateViewModel> Nodes => _nodes;

    /// <summary>
    /// Combined 1-13 RF scan results aggregated from all participating nodes.
    /// </summary>
    public IReadOnlyDictionary<int, RfChannelAggregate> CombinedRfScan => _combinedRfScan;

    /// <summary>
    /// The current channel and target MAC recommendation.
    /// </summary>
    public RfRecommendation? LatestRecommendation { get; private set; }

    /// <summary>
    /// True while a distributed RF sweep is in progress.
    /// </summary>
    public bool IsDistributedSweepActive { get; private set; }

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

    /// <summary>
    /// Returns the MAC addresses of all currently connected nodes.
    /// </summary>
    public IReadOnlyCollection<string> GetConnectedMacs()
    {
        return _nodes.Values
            .Where(n => !n.IsDisconnected && !string.IsNullOrWhiteSpace(n.Mac))
            .Select(n => n.Mac!)
            .ToList();
    }

    /// <summary>
    /// Marks the saved feature flags as already pushed for a node, so the
    /// background restore logic does not duplicate a set_features command the
    /// Blazor UI just sent.
    /// </summary>
    public void MarkFeaturesConfigured(string mac)
    {
        _featuresRestored[mac] = 0;
    }

    /// <summary>
    /// Starts a parallel 1-13 RF sweep across the supplied MAC addresses.
    /// Channels are round-robined across the nodes; the host waits for each
    /// <c>rf_scan</c> payload before sending the next channel in a node's queue.
    /// </summary>
    public Task StartDistributedSweepAsync(
        IReadOnlyCollection<string> macs,
        int dwellMs = 250,
        bool resetAggregates = true,
        CancellationToken cancellationToken = default)
    {
        if (macs.Count == 0)
        {
            return Task.CompletedTask;
        }

        StopDistributedSweep();

        _sweepDwellMs = dwellMs;

        if (resetAggregates)
        {
            _combinedRfScan.Clear();
            LatestRecommendation = null;
        }
        _sweepAssignments.Clear();
        _sweepAwaiting.Clear();

        var channels = Enumerable.Range(1, 13).ToList();
        var macList = macs.ToList();

        for (int i = 0; i < channels.Count; i++)
        {
            var mac = macList[i % macList.Count];
            _sweepAssignments.AddOrUpdate(
                mac,
                _ => new ConcurrentQueue<int>(new[] { channels[i] }),
                (_, existing) =>
                {
                    existing.Enqueue(channels[i]);
                    return existing;
                });
        }

        _sweepTcs = new TaskCompletionSource();
        cancellationToken.Register(() =>
        {
            StopDistributedSweep();
            _sweepTcs?.TrySetCanceled();
        });

        IsDistributedSweepActive = true;

        foreach (var mac in macList)
        {
            if (_sweepAssignments.TryGetValue(mac, out var queue) && queue.TryPeek(out var firstChannel))
            {
                if (TrySendSetRf(mac, firstChannel, _sweepDwellMs))
                {
                    _sweepAwaiting[mac] = firstChannel;
                }
                else
                {
                    _logger.LogWarning("Failed to send initial set_rf to {Mac}; removing this node from the sweep.", mac);
                    _sweepAssignments.TryRemove(mac, out _);
                }
            }
        }

        return _sweepTcs.Task;
    }

    /// <summary>
    /// Cancels an in-progress distributed RF sweep.
    /// </summary>
    public void StopDistributedSweep()
    {
        IsDistributedSweepActive = false;
        _sweepAssignments.Clear();
        _sweepAwaiting.Clear();
        _sweepTcs?.TrySetCanceled();
        _sweepTcs = null;
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
                ApplyErrors(existing, key);
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
                // A single malformed event must not kill the loop; without this
                // catch the task faults silently and state updates stop forever.
                try
                {
                    var key = state.Mac ?? state.PortName;

                    _configurationService.TryGetConfiguration(state.Mac ?? string.Empty, out var existingConfig);

                    if (state.Mac is not null && state.State == NodeConnectionState.Disconnected)
                    {
                        ClearErrors(state.Mac);
                    }

                    _nodes.AddOrUpdate(
                        key,
                        _ =>
                        {
                            var node = new NodeStateViewModel { Key = key };
                            ApplyConnectionState(node, state, existingConfig);
                            return node;
                        },
                        (_, existing) =>
                        {
                            ApplyConnectionState(existing, state, existingConfig);
                            return existing;
                        });

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

                    if (state.Mac is not null &&
                        state.State == NodeConnectionState.Standby &&
                        ShouldRestore(state, existingConfig))
                    {
                        await TryRestoreFeaturesAsync(state, existingConfig, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process state change for {Port}.", state.PortName);
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
                // A single malformed payload must not kill the loop; without this
                // catch the task faults silently and telemetry stops forever.
                try
                {
                    // Nacked commands are published by AckHandler so failures are
                    // visible even when the command was sent fire-and-forget.
                    if (payload.Type == "error" || (payload.Type == "ack" && payload.Success == false))
                    {
                        HandleErrorPayload(payload);
                        continue;
                    }

                if (payload.Type == "boot" && !string.IsNullOrWhiteSpace(payload.Mac))
                {
                    _logger.LogInformation("Node {Mac} reported boot; clearing active errors.", payload.Mac);
                    ClearErrors(payload.Mac);
                    ClearDiagnostics(payload.Mac);
                }

                if (payload.Type == "rf_scan" && !string.IsNullOrWhiteSpace(payload.Mac) && payload.Rf is not null)
                {
                    _rfScanResults.AddOrUpdate(
                        payload.Mac,
                        _ => new Dictionary<int, RfChannelMetrics> { [payload.Rf.Channel] = payload.Rf },
                        (_, existing) =>
                        {
                            existing[payload.Rf.Channel] = payload.Rf;
                            return existing;
                        });

                    _nodes.AddOrUpdate(
                        payload.Mac,
                        _ => new NodeStateViewModel
                        {
                            Key = payload.Mac,
                            PortName = payload.PortName ?? string.Empty,
                            Mac = payload.Mac,
                            State = NodeConnectionState.Standby,
                            RfScan = GetRfSnapshot(payload.Mac),
                        },
                        (_, existing) =>
                        {
                            ApplyRfScan(existing, payload.Mac);
                            return existing;
                        });

                    AggregateRfScan(payload.Rf);
                    AggregateAndAdvanceSweep(payload);
                }

                if (payload.Type == "diag" && payload.Test == "sync" && !string.IsNullOrWhiteSpace(payload.Mac) && payload.SyncDiag is not null)
                {
                    _nodes.AddOrUpdate(
                        payload.Mac,
                        _ => new NodeStateViewModel
                        {
                            Key = payload.Mac,
                            PortName = payload.PortName ?? string.Empty,
                            Mac = payload.Mac,
                            State = NodeConnectionState.Standby,
                            SyncDiag = payload.SyncDiag,
                        },
                        (_, existing) =>
                        {
                            ApplySyncDiagnostics(existing, payload.SyncDiag);
                            return existing;
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to process {Type} payload from {Port}.",
                        payload.Type,
                        payload.PortName ?? "unknown");
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
        if (string.IsNullOrWhiteSpace(payload.Cmd))
        {
            return;
        }

        var mac = payload.Mac;
        if (string.IsNullOrWhiteSpace(mac) && !string.IsNullOrWhiteSpace(payload.PortName))
        {
            mac = _nodes.Values
                .FirstOrDefault(n =>
                    string.Equals(n.PortName, payload.PortName, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(n.Mac))?.Mac;
        }

        if (string.IsNullOrWhiteSpace(mac))
        {
            _logger.LogWarning(
                "Received error payload from {Port}: cmd={Cmd}, reason={Reason}, param={Param}",
                payload.PortName ?? "unknown",
                payload.Cmd,
                payload.Reason,
                payload.Param);
            return;
        }

        var feature = GetFeatureKey(payload) ?? payload.Cmd;
        var reason = payload.Reason ?? "unknown";

        _activeErrors.AddOrUpdate(
            mac,
            _ => new ConcurrentDictionary<string, string>(new[] { new KeyValuePair<string, string>(feature, reason) }),
            (_, d) =>
            {
                d[feature] = reason;
                return d;
            });

        if (payload.Cmd == "set_features")
        {
            _unavailableFeatures.AddOrUpdate(
                mac,
                _ => new HashSet<string> { feature },
                (_, s) =>
                {
                    s.Add(feature);
                    return s;
                });
        }

        _nodes.AddOrUpdate(
            mac,
            _ => new NodeStateViewModel
            {
                Key = mac,
                PortName = payload.PortName ?? string.Empty,
                Mac = mac,
                State = NodeConnectionState.Standby,
                ActiveErrors = GetSnapshot(mac),
            },
            (_, existing) =>
            {
                ApplyErrors(existing, mac);
                return existing;
            });

        _logger.LogWarning(
            "Command {Feature} failed for {Mac} on {Port}: {Reason}.",
            feature,
            mac,
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
            "sync_not_armed" => "clock_leader",
            _ => null
        };
    }

    private void ApplyConnectionState(
        NodeStateViewModel node,
        NodeStateChanged state,
        NodeConfiguration? configuration)
    {
        node.PortName = state.PortName;
        node.Mac = state.Mac ?? node.Mac;
        node.State = state.State;
        node.Uptime = state.Uptime ?? node.Uptime;
        node.LastSeen = state.ReceivedAt ?? state.Timestamp;
        node.ClockLeader = state.ClockLeader ?? node.ClockLeader;
        node.ImuHost = state.ImuHost ?? node.ImuHost;
        node.Bandwidth = state.Bandwidth ?? configuration?.Bandwidth ?? node.Bandwidth;
        node.Configuration = configuration;

        if (node.Mac is not null && configuration is not null)
        {
            if (state.ClockLeader.HasValue &&
                configuration.ClockLeader.HasValue &&
                state.ClockLeader.Value == configuration.ClockLeader.Value)
            {
                _activeErrors.TryGetValue(node.Mac, out var clockErrors);
                clockErrors?.TryRemove("clock_leader", out _);
                RemoveUnavailable(node.Mac, "clock_leader");
            }

            if (state.ImuHost.HasValue &&
                configuration.ImuHost.HasValue &&
                state.ImuHost.Value == configuration.ImuHost.Value)
            {
                _activeErrors.TryGetValue(node.Mac, out var imuErrors);
                imuErrors?.TryRemove("imu_host", out _);
                RemoveUnavailable(node.Mac, "imu_host");
            }

            if (_activeErrors.TryGetValue(node.Mac, out var errors) && errors.IsEmpty)
            {
                _activeErrors.TryRemove(node.Mac, out _);
            }
        }

        if (node.Mac is not null)
        {
            ApplyErrors(node, node.Mac);
        }

        if (state.State != NodeConnectionState.DiagSync)
        {
            node.SyncDiag = null;
        }
    }

    private void ApplySyncDiagnostics(NodeStateViewModel node, SyncDiagnosticMetrics? syncDiag)
    {
        node.SyncDiag = syncDiag;
    }

    private void ApplyRfScan(NodeStateViewModel node, string mac)
    {
        node.RfScan = GetRfSnapshot(mac);
    }

    private void ApplyErrors(NodeStateViewModel node, string mac)
    {
        node.ActiveErrors = GetSnapshot(mac);
    }

    private void ClearDiagnostics(string mac)
    {
        _nodes.AddOrUpdate(
            mac,
            _ => new NodeStateViewModel { Key = mac, Mac = mac },
            (_, existing) =>
            {
                existing.SyncDiag = null;
                existing.RfScan = new Dictionary<int, RfChannelMetrics>();
                return existing;
            });

        _rfScanResults.TryRemove(mac, out _);
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

    private Dictionary<int, RfChannelMetrics> GetRfSnapshot(string mac)
    {
        if (!_rfScanResults.TryGetValue(mac, out var results))
        {
            return new Dictionary<int, RfChannelMetrics>();
        }

        return new Dictionary<int, RfChannelMetrics>(results);
    }

    private void ClearErrors(string mac)
    {
        _activeErrors.TryRemove(mac, out _);
        _unavailableFeatures.TryRemove(mac, out _);
        _lastRestore.TryRemove(mac, out _);
        _featuresRestored.TryRemove(mac, out _);
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
        await PushSetFeaturesAsync(state.PortName, state.Mac, configuration, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PushSetFeaturesAsync(
        string portName,
        string mac,
        NodeConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            cmd = "set_features",
            clock_leader = configuration.ClockLeader,
            imu_host = configuration.ImuHost,
        });

        Ack? ack;
        try
        {
            ack = await _portManager.SendCommandAsync(portName, json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "set_features to {Mac} on {Port} failed.", mac, portName);
            ack = null;
        }

        if (ack is null || !ack.Success)
        {
            _logger.LogWarning(
                "Failed to push feature flags to {Mac} on {Port}: {Reason}.",
                mac,
                portName,
                ack?.Reason ?? "no response");

            _lastRestore.TryRemove(mac, out _);
            return false;
        }

        _logger.LogInformation(
            "Pushed feature flags to {Mac} on {Port} (clock_leader={ClockLeader}, imu_host={ImuHost}).",
            mac,
            portName,
            configuration.ClockLeader,
            configuration.ImuHost);

        _featuresRestored[mac] = 0;
        return true;
    }

    private bool ShouldRestore(NodeStateChanged state, NodeConfiguration? configuration)
    {
        if (configuration is null || state.Mac is null)
        {
            return false;
        }

        bool clockConfigured = configuration.ClockLeader.HasValue;
        bool imuConfigured = configuration.ImuHost.HasValue;

        bool clockMismatch = state.ClockLeader.HasValue && state.ClockLeader.Value != configuration.ClockLeader;
        bool imuMismatch = state.ImuHost.HasValue && state.ImuHost.Value != configuration.ImuHost;

        bool clockUnavailable = IsUnavailable(state.Mac, "clock_leader");
        bool imuUnavailable = IsUnavailable(state.Mac, "imu_host");

        bool restored = _featuresRestored.ContainsKey(state.Mac);

        return (clockConfigured && !clockUnavailable && (clockMismatch || !restored)) ||
               (imuConfigured && !imuUnavailable && (imuMismatch || !restored));
    }

    private void AggregateRfScan(RfChannelMetrics metrics)
    {
        _combinedRfScan.AddOrUpdate(
            metrics.Channel,
            _ => new RfChannelAggregate
            {
                Channel = metrics.Channel,
                RssiMin = metrics.RssiMin,
                RssiMax = metrics.RssiMax,
                RssiAvg = metrics.RssiAvg,
                Packets = metrics.Packets,
                Errors = metrics.Errors,
                DurationMs = metrics.DurationMs,
                SampleCount = 1,
                TopMacs = metrics.TopMacs?.ToDictionary(m => m.Mac ?? string.Empty, m => m, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase)
            },
            (_, existing) => Merge(existing, metrics));

        LatestRecommendation = _evaluator.Recommend(_combinedRfScan);
    }

    private static RfChannelAggregate Merge(RfChannelAggregate existing, RfChannelMetrics metrics)
    {
        var packets = existing.Packets + metrics.Packets;
        var rssiAvg = packets > 0
            ? ((existing.RssiAvg * existing.Packets) + (metrics.RssiAvg * metrics.Packets)) / packets
            : 0.0;

        var topMacs = new Dictionary<string, RfMacMetrics>(existing.TopMacs, StringComparer.OrdinalIgnoreCase);
        MergeMacs(topMacs, metrics.TopMacs);

        return new RfChannelAggregate
        {
            Channel = existing.Channel,
            RssiMin = Math.Min(existing.RssiMin, metrics.RssiMin),
            RssiMax = Math.Max(existing.RssiMax, metrics.RssiMax),
            RssiAvg = rssiAvg,
            Packets = packets,
            Errors = existing.Errors + metrics.Errors,
            DurationMs = Math.Max(existing.DurationMs, metrics.DurationMs),
            SampleCount = existing.SampleCount + 1,
            TopMacs = topMacs
        };
    }

    private static void MergeMacs(Dictionary<string, RfMacMetrics> destination, List<RfMacMetrics>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var mac in source)
        {
            if (string.IsNullOrWhiteSpace(mac.Mac))
            {
                continue;
            }

            if (destination.TryGetValue(mac.Mac, out var existing))
            {
                var packets = existing.Packets + mac.Packets;
                var rssiAvg = packets > 0
                    ? ((existing.RssiAvg * existing.Packets) + (mac.RssiAvg * mac.Packets)) / packets
                    : 0.0;

                destination[mac.Mac] = new RfMacMetrics
                {
                    Mac = mac.Mac,
                    Packets = packets,
                    Errors = existing.Errors + mac.Errors,
                    RssiMin = Math.Min(existing.RssiMin, mac.RssiMin),
                    RssiMax = Math.Max(existing.RssiMax, mac.RssiMax),
                    RssiAvg = rssiAvg,
                    DurationMs = existing.DurationMs + mac.DurationMs
                };
            }
            else
            {
                destination[mac.Mac] = mac;
            }
        }
    }

    private void AggregateAndAdvanceSweep(NodePayload payload)
    {
        if (payload.Mac is null || payload.Rf is null)
        {
            return;
        }

        if (!_sweepAwaiting.TryGetValue(payload.Mac, out var expectedChannel) || expectedChannel != payload.Rf.Channel)
        {
            return;
        }

        _sweepAwaiting.TryRemove(payload.Mac, out _);

        if (_sweepAssignments.TryGetValue(payload.Mac, out var queue))
        {
            queue.TryDequeue(out _);
        }

        if (_sweepAssignments.TryGetValue(payload.Mac, out queue) && queue.TryPeek(out var nextChannel))
        {
            if (TrySendSetRf(payload.Mac, nextChannel, _sweepDwellMs))
            {
                _sweepAwaiting[payload.Mac] = nextChannel;
            }
            else
            {
                _logger.LogWarning("Failed to send set_rf ch {Channel} to {Mac}; removing this node from the sweep.", nextChannel, payload.Mac);
                _sweepAssignments.TryRemove(payload.Mac, out _);
            }
        }

        if (_sweepAwaiting.IsEmpty && _sweepAssignments.All(kv => kv.Value.IsEmpty))
        {
            IsDistributedSweepActive = false;
            _sweepTcs?.TrySetResult();
            _sweepTcs = null;
        }
    }

    private bool TrySendSetRf(string mac, int channel, int dwellMs)
    {
        if (!TryGetPortName(mac, out var portName) || string.IsNullOrWhiteSpace(portName))
        {
            _logger.LogWarning("Cannot send set_rf to {Mac}: port not found.", mac);
            return false;
        }

        var json = JsonSerializer.Serialize(new { cmd = "set_rf", ch = channel, dwell_ms = dwellMs });
        _logger.LogDebug("Queueing set_rf for {Mac} on {Port}: {Command}", mac, portName, json);
        return _portManager.TrySendCommand(portName, json);
    }

    private bool TrySendSetRfPassive(string mac, int channel, int bw, IReadOnlyCollection<string> targetMacs)
    {
        if (!TryGetPortName(mac, out var portName) || string.IsNullOrWhiteSpace(portName))
        {
            _logger.LogWarning("Cannot send passive set_rf to {Mac}: port not found.", mac);
            return false;
        }

        if (targetMacs.Count == 0)
        {
            _logger.LogWarning("TrySendSetRfPassive for {Mac} rejected: no target MACs.", mac);
            return false;
        }

        var json = JsonSerializer.Serialize(new
        {
            cmd = "set_rf",
            ch = channel,
            bw,
            mode = "passive",
            mac_filter = targetMacs
        });

        _logger.LogDebug("Queueing passive set_rf for {Mac} on {Port}: {Command}", mac, portName, json);
        return _portManager.TrySendCommand(portName, json);
    }

    /// <summary>
    /// Broadcasts a passive sniffing configuration to all connected nodes, attempting
    /// to transition them into streaming mode simultaneously.
    /// </summary>
    public Task BroadcastLockAndStreamAsync(int channel, int bw, IReadOnlyCollection<string> targetMacs, CancellationToken cancellationToken = default)
    {
        var macs = GetConnectedMacs();
        if (macs.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (targetMacs.Count == 0)
        {
            _logger.LogWarning("BroadcastLockAndStreamAsync rejected: no target MACs.");
            return Task.CompletedTask;
        }

        var tasks = new List<Task>(macs.Count);
        foreach (var mac in macs)
        {
            var captured = mac;
            tasks.Add(Task.Run(() => TrySendSetRfPassive(captured, channel, bw, targetMacs), cancellationToken));
        }

        return Task.WhenAll(tasks);
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
