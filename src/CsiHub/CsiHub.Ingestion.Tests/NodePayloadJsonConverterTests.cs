using System.Text.Json;
using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.Tests;

public class NodePayloadJsonConverterTests
{
    [Fact]
    public void Can_Parse_Compact_Flat_Array_Payload()
    {
        const string json = """{"t":12345,"c":[1.0,2.0,3.0],"i":[0.1,0.2]}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal(12345L, payload.Timestamp);
        Assert.NotNull(payload.Csi);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, payload.Csi);
        Assert.NotNull(payload.Imu);
        Assert.Equal(new[] { 0.1, 0.2 }, payload.Imu);
    }

    [Fact]
    public void Can_Parse_Heartbeat_With_Long_Keys()
    {
        const string json = """{"type":"hb","mac":"AA:BB:CC:DD:EE:FF","state":"standby","uptime":5}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("hb", payload.Type);
        Assert.Equal("AA:BB:CC:DD:EE:FF", payload.Mac);
        Assert.Equal("standby", payload.State);
    }

    [Fact]
    public void Can_Parse_Heartbeat_With_Feature_Flags()
    {
        const string json = """{"type":"hb","mac":"AA:BB:CC:DD:EE:FF","state":"standby","uptime":5,"clock_leader":true,"imu_host":false}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("hb", payload.Type);
        Assert.True(payload.ClockLeader);
        Assert.False(payload.ImuHost);
    }

    [Fact]
    public void Can_Parse_Heartbeat_Bandwidth()
    {
        const string json = """{"type":"hb","mac":"AA:BB:CC:DD:EE:FF","state":"standby","uptime":5,"bw":40,"clock_leader":true,"imu_host":false}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("hb", payload.Type);
        Assert.Equal(40, payload.Bandwidth);
    }

    [Fact]
    public void Can_Parse_Error_Payload_With_Param()
    {
        const string json = """{"type":"error","cmd":"set_features","param":"imu_host","reason":"init_failed"}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("error", payload.Type);
        Assert.Equal("set_features", payload.Cmd);
        Assert.Equal("imu_host", payload.Param);
        Assert.Equal("init_failed", payload.Reason);
    }

    [Fact]
    public void Can_Parse_Heartbeat_Only_Type()
    {
        const string json = """{"type":"hb"}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("hb", payload.Type);
    }

    [Fact]
    public void Can_Parse_Csi_Payload()
    {
        const string json = """{"type":"csi","mac":"AA:BB:CC:DD:EE:FF","t":12345,"c":[1.0,2.0,3.0,4.0,5.0]}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("csi", payload.Type);
        Assert.Equal("AA:BB:CC:DD:EE:FF", payload.Mac);
        Assert.Equal(12345L, payload.Timestamp);
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }, payload.Csi);
    }

    [Fact]
    public void Can_Parse_Imu_Payload()
    {
        const string json = """{"type":"imu","mac":"AA:BB:CC:DD:EE:FF","t":12345,"i":[0.1,0.2,0.3,0.4]}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("imu", payload.Type);
        Assert.Equal("AA:BB:CC:DD:EE:FF", payload.Mac);
        Assert.Equal(12345L, payload.Timestamp);
        Assert.Equal(new[] { 0.1, 0.2, 0.3, 0.4 }, payload.Imu);
    }

    [Fact]
    public void Can_Parse_Config_Response()
    {
        const string json = """{"type":"config","mac":"00:11:22:33:44:55","state":"standby","baud":921600,"version":"0.1.0"}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("config", payload.Type);
        Assert.Equal("00:11:22:33:44:55", payload.Mac);
        Assert.Equal("standby", payload.State);
    }

    [Fact]
    public void Ignores_Unknown_Properties()
    {
        const string json = """{"type":"ack","cmd":"set_features","success":true,"state":"standby"}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("ack", payload.Type);
        Assert.Equal("standby", payload.State);
        Assert.Null(payload.Csi);
        Assert.Null(payload.Imu);
    }
}
