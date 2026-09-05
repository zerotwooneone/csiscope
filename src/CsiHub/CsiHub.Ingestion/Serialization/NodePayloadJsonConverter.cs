using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsiHub.Ingestion.Models;

namespace CsiHub.Ingestion.Serialization;

/// <summary>
/// High-performance, low-allocation converter for the CsiScope NDJSON payload schema.
/// Accepts both long and compact property names and uses <see cref="ArrayPool{T}"/>
/// while reading the flat CSI and IMU arrays.
/// </summary>
public sealed class NodePayloadJsonConverter : JsonConverter<NodePayload>
{
    public override NodePayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object at the root of a node payload.");
        }

        var payload = new NodePayload();
        double? imuW = null;
        double? imuX = null;
        double? imuY = null;
        double? imuZ = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a property name inside the node payload object.");
            }

            string? propertyName = reader.GetString();

            if (!reader.Read() || propertyName is null)
            {
                throw new JsonException("Unexpected end of JSON object.");
            }

            switch (propertyName)
            {
                case "type":
                    payload.Type = reader.GetString();
                    break;

                case "m":
                case "mac":
                    payload.Mac = reader.GetString();
                    break;

                case "t":
                case "timestamp":
                case "uptime":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long timestamp))
                    {
                        payload.Timestamp = timestamp;
                    }
                    break;

                case "state":
                    payload.State = reader.GetString();
                    break;

                case "cmd":
                    payload.Cmd = reader.GetString();
                    break;

                case "param":
                case "feature":
                    payload.Param = reader.GetString();
                    break;

                case "reason":
                    payload.Reason = reader.GetString();
                    break;

                case "success":
                    if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                    {
                        payload.Success = reader.GetBoolean();
                    }
                    break;

                case "test":
                case "diag_type":
                    payload.Test = reader.GetString();
                    break;

                case "pulse_count":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var pulseCount))
                    {
                        EnsureSyncDiag(payload).PulseCount = pulseCount;
                    }
                    break;

                case "latency_us":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var latencyUs))
                    {
                        EnsureSyncDiag(payload).LatencyUs = latencyUs;
                    }
                    break;

                case "jitter_us":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var jitterUs))
                    {
                        EnsureSyncDiag(payload).JitterUs = jitterUs;
                    }
                    break;

                case "clock_leader":
                    if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                    {
                        payload.ClockLeader = reader.GetBoolean();
                    }
                    break;

                case "imu_host":
                    if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                    {
                        payload.ImuHost = reader.GetBoolean();
                    }
                    break;

                case "bw":
                case "bandwidth":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var bw))
                    {
                        payload.Bandwidth = bw;
                    }
                    break;

                case "c":
                case "csi":
                    payload.Csi = ReadDoubleArray(ref reader);
                    break;

                case "nz":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var csiNz))
                    {
                        payload.CsiNonZero = csiNz;
                    }
                    break;

                case "blen":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var csiBufLen))
                    {
                        payload.CsiBufLen = csiBufLen;
                    }
                    break;

                case "fwi":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var csiFwi))
                    {
                        payload.CsiFirstWordInvalid = csiFwi != 0;
                    }
                    break;

                case "s":
                case "seq":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var seq))
                    {
                        payload.Seq = seq;
                    }
                    break;

                case "rssi":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var rssi))
                    {
                        payload.Rssi = rssi;
                    }
                    break;

                case "src":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out var srcMac))
                    {
                        payload.SrcMac = srcMac;
                    }
                    break;

                case "i":
                case "imu":
                    payload.Imu = ReadDoubleArray(ref reader);
                    break;

                case "qw":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var qw))
                    {
                        imuW = qw;
                    }
                    break;

                case "qx":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var qx))
                    {
                        imuX = qx;
                    }
                    break;

                case "qy":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var qy))
                    {
                        imuY = qy;
                    }
                    break;

                case "qz":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var qz))
                    {
                        imuZ = qz;
                    }
                    break;

                case "ch":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ch))
                    {
                        EnsureRf(payload).Channel = ch;
                    }
                    break;

                case "rssi_min":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var rssiMin))
                    {
                        EnsureRf(payload).RssiMin = rssiMin;
                    }
                    break;

                case "rssi_max":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var rssiMax))
                    {
                        EnsureRf(payload).RssiMax = rssiMax;
                    }
                    break;

                case "rssi_avg":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var rssiAvg))
                    {
                        EnsureRf(payload).RssiAvg = rssiAvg;
                    }
                    break;

                case "packets":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var packets))
                    {
                        EnsureRf(payload).Packets = packets;
                    }
                    break;

                case "errors":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var errors))
                    {
                        EnsureRf(payload).Errors = errors;
                    }
                    break;

                case "duration_ms":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var durationMs))
                    {
                        EnsureRf(payload).DurationMs = durationMs;
                    }
                    break;

                case "top_macs":
                    EnsureRf(payload).TopMacs = ReadTopMacs(ref reader);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        if (payload.Rf is not null && payload.Rf.DurationMs > 0 && payload.Rf.TopMacs is not null)
        {
            foreach (var mac in payload.Rf.TopMacs)
            {
                if (mac is not null)
                {
                    mac.DurationMs = payload.Rf.DurationMs;
                }
            }
        }

        if (imuW.HasValue && imuX.HasValue && imuY.HasValue && imuZ.HasValue)
        {
            payload.Imu = new[] { imuW.Value, imuX.Value, imuY.Value, imuZ.Value };
        }

        return payload;
    }

    private static RfChannelMetrics EnsureRf(NodePayload payload)
    {
        return payload.Rf ??= new RfChannelMetrics();
    }

    private static SyncDiagnosticMetrics EnsureSyncDiag(NodePayload payload)
    {
        return payload.SyncDiag ??= new SyncDiagnosticMetrics();
    }

    private static List<RfMacMetrics>? ReadTopMacs(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return null;
        }

        var topMacs = new List<RfMacMetrics>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var mac = new RfMacMetrics();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        reader.Skip();
                        continue;
                    }

                    string prop = reader.GetString() ?? string.Empty;
                    reader.Read();

                    switch (prop)
                    {
                        case "mac":
                            mac.Mac = reader.GetString();
                            break;
                        case "packets":
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var packets))
                            {
                                mac.Packets = packets;
                            }
                            break;
                        case "errors":
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var errors))
                            {
                                mac.Errors = errors;
                            }
                            break;
                        case "rssi_min":
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var rssiMin))
                            {
                                mac.RssiMin = rssiMin;
                            }
                            break;
                        case "rssi_max":
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var rssiMax))
                            {
                                mac.RssiMax = rssiMax;
                            }
                            break;
                        case "rssi_avg":
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var rssiAvg))
                            {
                                mac.RssiAvg = rssiAvg;
                            }
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }

                topMacs.Add(mac);
            }
            else
            {
                reader.Skip();
            }
        }

        return topMacs;
    }

    private static double[]? ReadDoubleArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return null;
        }

        const int InitialCapacity = 64;
        double[] rented = ArrayPool<double>.Shared.Rent(InitialCapacity);
        int count = 0;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.Number)
                {
                    if (count == rented.Length)
                    {
                        double[] next = ArrayPool<double>.Shared.Rent(rented.Length * 2);
                        Array.Copy(rented, next, rented.Length);
                        ArrayPool<double>.Shared.Return(rented, clearArray: false);
                        rented = next;
                    }

                    rented[count++] = reader.GetDouble();
                }
                else
                {
                    // Skip non-numeric elements; we only care about the flat numeric array.
                    reader.Skip();
                }
            }

            if (count == 0)
            {
                return null;
            }

            double[] result = new double[count];
            Array.Copy(rented, result, count);
            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented, clearArray: false);
        }
    }

    public override void Write(Utf8JsonWriter writer, NodePayload value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.Type is not null)
        {
            writer.WriteString("type", value.Type);
        }

        if (value.Mac is not null)
        {
            writer.WriteString("mac", value.Mac);
        }

        if (value.Timestamp.HasValue)
        {
            writer.WriteNumber("timestamp", value.Timestamp.Value);
        }

        if (value.State is not null)
        {
            writer.WriteString("state", value.State);
        }

        if (value.Cmd is not null)
        {
            writer.WriteString("cmd", value.Cmd);
        }

        if (value.Param is not null)
        {
            writer.WriteString("param", value.Param);
        }

        if (value.Reason is not null)
        {
            writer.WriteString("reason", value.Reason);
        }

        if (value.Test is not null)
        {
            writer.WriteString("test", value.Test);
        }

        if (value.Success.HasValue)
        {
            writer.WriteBoolean("success", value.Success.Value);
        }

        if (value.ClockLeader.HasValue)
        {
            writer.WriteBoolean("clock_leader", value.ClockLeader.Value);
        }

        if (value.ImuHost.HasValue)
        {
            writer.WriteBoolean("imu_host", value.ImuHost.Value);
        }

        if (value.Bandwidth.HasValue)
        {
            writer.WriteNumber("bw", value.Bandwidth.Value);
        }

        if (value.Csi is not null)
        {
            writer.WritePropertyName("csi");
            JsonSerializer.Serialize(writer, value.Csi, options);
        }

        if (value.CsiNonZero.HasValue)
        {
            writer.WriteNumber("nz", value.CsiNonZero.Value);
        }

        if (value.CsiBufLen.HasValue)
        {
            writer.WriteNumber("blen", value.CsiBufLen.Value);
        }

        if (value.CsiFirstWordInvalid.HasValue)
        {
            writer.WriteNumber("fwi", value.CsiFirstWordInvalid.Value ? 1 : 0);
        }

        if (value.Seq.HasValue)
        {
            writer.WriteNumber("seq", value.Seq.Value);
        }

        if (value.Rssi.HasValue)
        {
            writer.WriteNumber("rssi", value.Rssi.Value);
        }

        if (value.SrcMac.HasValue)
        {
            writer.WriteNumber("src", value.SrcMac.Value);
        }

        if (value.Imu is not null)
        {
            writer.WritePropertyName("imu");
            JsonSerializer.Serialize(writer, value.Imu, options);
        }

        if (value.Rf is not null)
        {
            writer.WriteNumber("ch", value.Rf.Channel);
            writer.WriteNumber("rssi_min", value.Rf.RssiMin);
            writer.WriteNumber("rssi_max", value.Rf.RssiMax);
            writer.WriteNumber("rssi_avg", value.Rf.RssiAvg);
            writer.WriteNumber("packets", value.Rf.Packets);
            writer.WriteNumber("errors", value.Rf.Errors);
            writer.WriteNumber("duration_ms", value.Rf.DurationMs);
        }

        writer.WriteEndObject();
    }
}
