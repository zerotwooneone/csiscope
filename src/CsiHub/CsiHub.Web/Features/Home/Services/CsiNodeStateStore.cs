using System.Collections.Concurrent;
using CsiHub.Features.Home.Models;
using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Hosting;

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
    private readonly ConcurrentDictionary<string, NodeStateViewModel> _nodes = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public CsiNodeStateStore(CsiIngestionChannel channel)
    {
        _channel = channel;
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

                _nodes.AddOrUpdate(
                    key,
                    _ => ToViewModel(state, key),
                    (_, existing) => ToViewModel(state, key, existing));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private static NodeStateViewModel ToViewModel(NodeStateChanged state, string key, NodeStateViewModel? existing = null)
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
        };
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
