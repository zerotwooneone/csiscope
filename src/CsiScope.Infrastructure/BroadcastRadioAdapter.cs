using System.Collections.Concurrent;
using System.Collections.Immutable;
using CsiScope.Application.Ports;
using CsiScope.Domain.Model;
using Microsoft.Extensions.Logging;

namespace CsiScope.Infrastructure;

/// <summary>
/// <see cref="IRadioCommandPort"/> composite: fans each command out to every
/// registered <see cref="NodeSerialAdapter"/> (one per physical port) and
/// awaits all of them. Success requires a majority quorum — a node that
/// NACKs or times out is logged but cannot veto the hop. Mirrors the legacy
/// per-port command semantics while presenting one port surface to the
/// orchestrator.
/// </summary>
public sealed class BroadcastRadioAdapter : IRadioCommandPort
{
    private readonly ConcurrentDictionary<string, NodeSerialAdapter> _nodes = new();
    private readonly ILogger<BroadcastRadioAdapter>? _logger;

    public BroadcastRadioAdapter(ILogger<BroadcastRadioAdapter>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Live node count — diagnostics.</summary>
    public int NodeCount => _nodes.Count;

    public void RegisterNode(string portKey, NodeSerialAdapter adapter) => _nodes[portKey] = adapter;

    public void UnregisterNode(string portKey) => _nodes.TryRemove(portKey, out _);

    /// <summary>Routes an ack frame to the adapter that owns the originating port.</summary>
    public void NotifyAck(string portKey, long seq, bool success, string? reason = null)
    {
        if (!success)
        {
            _logger?.LogWarning(
                "NACK from {Port} seq {Seq}: {Reason}", portKey, seq, reason ?? "unspecified");
        }

        if (_nodes.TryGetValue(portKey, out var adapter))
        {
            adapter.NotifyAck(seq, success);
        }
    }

    public async Task<bool> BroadcastSetRfAsync(WifiChannel channel, ImmutableArray<MacAddress> macFilter, CancellationToken ct = default)
    {
        var nodes = _nodes.ToArray();
        if (nodes.Length == 0)
        {
            return false;
        }

        var results = await Task.WhenAll(
            nodes.Select(kv => SafeSendAsync(kv.Key, kv.Value, channel, macFilter, ct)));

        var succeeded = results.Count(r => r);
        return succeeded * 2 > nodes.Length; // majority quorum
    }

    private async Task<bool> SafeSendAsync(
        string portKey,
        NodeSerialAdapter adapter,
        WifiChannel channel,
        ImmutableArray<MacAddress> macFilter,
        CancellationToken ct)
    {
        try
        {
            var ok = await adapter.SendSetRfAsync(channel, macFilter, ct);
            if (!ok)
            {
                _logger?.LogWarning("set_rf ch {Channel} not acknowledged by {Port}", (int)channel, portKey);
            }

            return ok;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "set_rf ch {Channel} failed on {Port}", (int)channel, portKey);
            return false;
        }
    }
}
