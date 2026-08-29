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
        const string json = """{"type":"hb","mac":"AA:BB:CC:DD:EE:FF","role":"leader","state":"standby","uptime":5}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("hb", payload.Type);
        Assert.Equal("AA:BB:CC:DD:EE:FF", payload.Mac);
        Assert.Equal("standby", payload.State);
    }

    [Fact]
    public void Can_Parse_Config_Response()
    {
        const string json = """{"type":"config","mac":"00:11:22:33:44:55","state":"assigned","baud":921600,"version":"0.1.0"}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("config", payload.Type);
        Assert.Equal("00:11:22:33:44:55", payload.Mac);
        Assert.Equal("assigned", payload.State);
    }

    [Fact]
    public void Ignores_Unknown_Properties()
    {
        const string json = """{"type":"ack","cmd":"set_role","success":true,"state":"assigned"}""";

        var payload = JsonSerializer.Deserialize<NodePayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("ack", payload.Type);
        Assert.Equal("assigned", payload.State);
        Assert.Null(payload.Csi);
        Assert.Null(payload.Imu);
    }
}
