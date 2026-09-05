using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Models;
using CsiHub.Ingestion.Pipelines;
using CsiHub.Ingestion.Pipelines.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion.Tests;

public class PayloadDispatcherTests
{
    private static (PayloadDispatcher Dispatcher, SerialPipelineContext Context, CsiIngestionChannel Channel) CreatePipeline()
    {
        var channel = new CsiIngestionChannel(Options.Create(new CsiIngestionOptions()));
        var context = new SerialPipelineContext("COM_TEST", channel, NullLogger.Instance);
        var dispatcher = new PayloadDispatcher(new IPayloadHandler[]
        {
            new ConfigHandler(),
            new HeartbeatHandler(),
            new AckHandler(),
            new TelemetryHandler(),
            new IgnoredHandler(),
        });
        return (dispatcher, context, channel);
    }

    [Fact]
    public void Config_Sets_HasSeenConfig_Updates_Mac_And_Publishes_State()
    {
        var (dispatcher, context, channel) = CreatePipeline();

        var payload = new NodePayload
        {
            Type = "config",
            Mac = "00:11:22:33:44:55",
            State = "standby",
        };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.True(context.HasSeenConfig);
        Assert.Equal("00:11:22:33:44:55", context.LastMac);

        Assert.True(channel.StateReader.TryRead(out var state));
        Assert.Equal("COM_TEST", state!.PortName);
        Assert.Equal("00:11:22:33:44:55", state.Mac);
        Assert.Equal(NodeConnectionState.Standby, state.State);
    }

    [Fact]
    public void Heartbeat_Publishes_State_With_Feature_Flags()
    {
        var (dispatcher, context, channel) = CreatePipeline();

        var payload = new NodePayload
        {
            Type = "hb",
            Mac = "00:11:22:33:44:55",
            State = "streaming",
            ClockLeader = true,
            ImuHost = true,
            Bandwidth = 40,
        };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.Equal("00:11:22:33:44:55", context.LastMac);

        Assert.True(channel.StateReader.TryRead(out var state));
        Assert.Equal(NodeConnectionState.Streaming, state!.State);
        Assert.True(state.ClockLeader);
        Assert.True(state.ImuHost);
        Assert.Equal(40, state.Bandwidth);
    }

    [Fact]
    public async Task Ack_Completes_Pending_Command()
    {
        var (dispatcher, context, _) = CreatePipeline();

        var tcs = new TaskCompletionSource<Ack>();
        context.PendingAcks[7] = tcs;

        var payload = new NodePayload
        {
            Type = "ack",
            Seq = 7,
            Cmd = "set_rf",
            Success = true,
            State = "streaming",
        };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        var ack = await tcs.Task;
        Assert.Equal(7, ack.Seq);
        Assert.Equal("set_rf", ack.Cmd);
        Assert.True(ack.Success);
        Assert.Empty(context.PendingAcks);
    }

    [Fact]
    public void Ack_Without_Pending_Seq_Does_Not_Throw()
    {
        var (dispatcher, context, _) = CreatePipeline();

        var payload = new NodePayload
        {
            Type = "ack",
            Seq = 99,
            Cmd = "set_rf",
            Success = false,
            Reason = "timeout",
        };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.Empty(context.PendingAcks);
    }

    [Theory]
    [InlineData("csi")]
    [InlineData("imu")]
    [InlineData("diag")]
    [InlineData("error")]
    [InlineData("rf_scan")]
    [InlineData("boot")]
    public void Telemetry_Types_Are_Published_To_Both_Channels(string type)
    {
        var (dispatcher, context, channel) = CreatePipeline();

        var payload = new NodePayload { Type = type, Mac = "00:11:22:33:44:55" };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.True(channel.StateStorePayloadReader.TryRead(out var stateStorePayload));
        Assert.Equal(type, stateStorePayload!.Type);

        Assert.True(channel.DspPayloadReader.TryRead(out var dspPayload));
        Assert.Equal(type, dspPayload!.Type);
    }

    [Theory]
    [InlineData("post")]
    [InlineData("mystery")]
    [InlineData(null)]
    public void Ignored_Types_Are_Not_Published(string? type)
    {
        var (dispatcher, context, channel) = CreatePipeline();

        var payload = new NodePayload { Type = type };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.False(channel.StateStorePayloadReader.TryRead(out _));
        Assert.False(channel.DspPayloadReader.TryRead(out _));
        Assert.False(channel.StateReader.TryRead(out _));
    }

    [Fact]
    public void Boot_With_State_Publishes_Standby()
    {
        var (dispatcher, context, channel) = CreatePipeline();

        var payload = new NodePayload
        {
            Type = "boot",
            Mac = "00:11:22:33:44:55",
            State = "boot",
        };

        dispatcher.Dispatch(payload, ReadOnlySpan<byte>.Empty, context);

        Assert.True(channel.StateStorePayloadReader.TryRead(out var published));
        Assert.Equal("boot", published!.Type);

        Assert.True(channel.StateReader.TryRead(out var state));
        Assert.Equal(NodeConnectionState.Standby, state!.State);
    }

    [Fact]
    public void First_Matching_Handler_Wins()
    {
        var channel = new CsiIngestionChannel(Options.Create(new CsiIngestionOptions()));
        var context = new SerialPipelineContext("COM_TEST", channel, NullLogger.Instance);
        var recording = new RecordingHandler();
        var dispatcher = new PayloadDispatcher(new IPayloadHandler[]
        {
            recording,
            new IgnoredHandler(),
        });

        dispatcher.Dispatch(new NodePayload { Type = "hb" }, ReadOnlySpan<byte>.Empty, context);

        Assert.Equal(1, recording.CallCount);
    }

    private sealed class RecordingHandler : IPayloadHandler
    {
        public int CallCount { get; private set; }

        public bool CanHandle(NodePayload payload) => true;

        public void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
            => CallCount++;
    }
}
