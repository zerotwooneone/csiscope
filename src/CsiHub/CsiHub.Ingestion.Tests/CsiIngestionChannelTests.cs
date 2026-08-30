using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion.Tests;

public class CsiIngestionChannelTests
{
    private static CsiIngestionChannel CreateChannel(int payloadCapacity = 16, int stateCapacity = 16)
        => new(Options.Create(new CsiIngestionOptions
        {
            PayloadChannelCapacity = payloadCapacity,
            StateChannelCapacity = stateCapacity
        }));

    [Fact]
    public void Can_Publish_And_Read_Payload()
    {
        var channel = CreateChannel();
        var payload = new NodePayload
        {
            Type = "hb",
            Mac = "AA:BB:CC:DD:EE:FF",
            State = "standby"
        };

        Assert.True(channel.TryPublish(payload));
        Assert.True(channel.StateStorePayloadReader.TryRead(out var received));

        Assert.NotNull(received);
        Assert.Equal("hb", received.Type);
        Assert.Equal("AA:BB:CC:DD:EE:FF", received.Mac);
    }

    [Fact]
    public void Can_Publish_And_Read_State_Change()
    {
        var channel = CreateChannel();
        var state = new NodeStateChanged("COM3", "AA:BB:CC:DD:EE:FF", NodeConnectionState.Assigned, DateTimeOffset.UtcNow);

        Assert.True(channel.TryPublishState(state));
        Assert.True(channel.StateReader.TryRead(out var received));

        Assert.NotNull(received);
        Assert.Equal("COM3", received.PortName);
        Assert.Equal(NodeConnectionState.Assigned, received.State);
    }

    [Fact]
    public void Bounded_Payload_Channel_Drops_Oldest_When_Full()
    {
        var channel = CreateChannel(payloadCapacity: 3);

        channel.TryPublish(new NodePayload { Type = "0" });
        channel.TryPublish(new NodePayload { Type = "1" });
        channel.TryPublish(new NodePayload { Type = "2" });
        channel.TryPublish(new NodePayload { Type = "3" }); // Should drop the oldest (0)

        var types = new List<string?>();
        while (channel.StateStorePayloadReader.TryRead(out var payload))
        {
            types.Add(payload!.Type);
        }

        Assert.Equal(new[] { "1", "2", "3" }, types);
    }

    [Fact]
    public void High_Rate_Payload_Channel_Drops_Oldest_Without_Throwing()
    {
        const int capacity = 16;
        const int publishCount = 1000;
        var channel = CreateChannel(payloadCapacity: capacity);

        for (int i = 0; i < publishCount; i++)
        {
            Assert.True(channel.TryPublish(new NodePayload { Type = i.ToString() }));
        }

        var types = new List<string?>();
        while (channel.StateStorePayloadReader.TryRead(out var payload))
        {
            types.Add(payload!.Type);
        }

        // Only the most recent 'capacity' items should remain.
        Assert.Equal(capacity, types.Count);
        Assert.Equal(Enumerable.Range(publishCount - capacity, capacity).Select(i => i.ToString()), types);
    }

    [Fact]
    public void TryPublish_Fans_Out_To_StateStore_And_Dsp_Channels()
    {
        var channel = CreateChannel();
        var payload = new NodePayload { Type = "csi", Mac = "AA:BB:CC:DD:EE:FF" };

        Assert.True(channel.TryPublish(payload));

        Assert.True(channel.StateStorePayloadReader.TryRead(out var stateStorePayload));
        Assert.True(channel.DspPayloadReader.TryRead(out var dspPayload));

        Assert.Equal("csi", stateStorePayload!.Type);
        Assert.Equal("csi", dspPayload!.Type);
        Assert.Equal("AA:BB:CC:DD:EE:FF", stateStorePayload.Mac);
        Assert.Equal("AA:BB:CC:DD:EE:FF", dspPayload.Mac);
    }
}
