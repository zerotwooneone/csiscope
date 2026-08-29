using System.Threading.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion.Channels;

/// <summary>
/// Singleton ingestion bus. Decouples the high-rate serial RX threads from the DSP
/// pipeline by publishing parsed payloads and node state changes to
/// <see cref="System.Threading.Channels.Channel{T}"/> readers.
/// Payloads are bounded with <see cref="BoundedChannelFullMode.DropOldest"/> so the
/// DSP pipeline can fall behind without causing an OOM exception.
/// </summary>
public sealed class CsiIngestionChannel
{
    private readonly Channel<NodePayload> _payloadChannel;
    private readonly Channel<NodeStateChanged> _stateChannel;

    public CsiIngestionChannel(IOptions<CsiIngestionOptions> options)
    {
        var value = options.Value;

        _payloadChannel = Channel.CreateBounded<NodePayload>(new BoundedChannelOptions(value.PayloadChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

        _stateChannel = Channel.CreateBounded<NodeStateChanged>(new BoundedChannelOptions(value.StateChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelReader<NodePayload> PayloadReader => _payloadChannel.Reader;

    public ChannelReader<NodeStateChanged> StateReader => _stateChannel.Reader;

    /// <summary>
    /// Attempts to publish a payload without ever blocking the serial RX thread.
    /// </summary>
    public bool TryPublish(NodePayload payload)
        => _payloadChannel.Writer.TryWrite(payload);

    /// <summary>
    /// Attempts to publish a node state change without blocking the serial RX thread.
    /// </summary>
    public bool TryPublishState(NodeStateChanged state)
        => _stateChannel.Writer.TryWrite(state);
}
