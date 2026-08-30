using System.Threading.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion.Channels;

/// <summary>
/// Singleton ingestion bus. Decouples the high-rate serial RX threads from the DSP
/// pipeline by publishing parsed payloads and node state changes to
/// <see cref="System.Threading.Channels.Channel{T}"/> readers.
/// Payloads are fanned-out to a state-store channel (UI/dashboard) and a DSP channel
/// (signal processing) so both consumers receive every payload.
/// </summary>
public sealed class CsiIngestionChannel
{
    private readonly Channel<NodePayload> _stateStorePayloadChannel;
    private readonly Channel<NodePayload> _dspPayloadChannel;
    private readonly Channel<NodeStateChanged> _stateChannel;

    public CsiIngestionChannel(IOptions<CsiIngestionOptions> options)
    {
        var value = options.Value;

        _stateStorePayloadChannel = Channel.CreateBounded<NodePayload>(new BoundedChannelOptions(value.PayloadChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _dspPayloadChannel = Channel.CreateBounded<NodePayload>(new BoundedChannelOptions(value.PayloadChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _stateChannel = Channel.CreateBounded<NodeStateChanged>(new BoundedChannelOptions(value.StateChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Channel consumed by the UI state store (CsiNodeStateStore).
    /// </summary>
    public ChannelReader<NodePayload> StateStorePayloadReader => _stateStorePayloadChannel.Reader;

    /// <summary>
    /// Channel consumed by the DSP pipeline (CsiDspBackgroundService).
    /// </summary>
    public ChannelReader<NodePayload> DspPayloadReader => _dspPayloadChannel.Reader;

    public ChannelReader<NodeStateChanged> StateReader => _stateChannel.Reader;

    /// <summary>
    /// Attempts to publish a payload to both the state store and DSP channels
    /// without ever blocking the serial RX thread.
    /// </summary>
    public bool TryPublish(NodePayload payload)
    {
        var writtenToStateStore = _stateStorePayloadChannel.Writer.TryWrite(payload);
        var writtenToDsp = _dspPayloadChannel.Writer.TryWrite(payload);
        return writtenToStateStore && writtenToDsp;
    }

    /// <summary>
    /// Attempts to publish a node state change without blocking the serial RX thread.
    /// </summary>
    public bool TryPublishState(NodeStateChanged state)
        => _stateChannel.Writer.TryWrite(state);
}
