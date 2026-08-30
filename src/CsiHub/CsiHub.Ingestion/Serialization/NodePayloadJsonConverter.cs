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

                case "c":
                case "csi":
                    payload.Csi = ReadDoubleArray(ref reader);
                    break;

                case "i":
                case "imu":
                    payload.Imu = ReadDoubleArray(ref reader);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return payload;
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

        if (value.ClockLeader.HasValue)
        {
            writer.WriteBoolean("clock_leader", value.ClockLeader.Value);
        }

        if (value.ImuHost.HasValue)
        {
            writer.WriteBoolean("imu_host", value.ImuHost.Value);
        }

        if (value.Csi is not null)
        {
            writer.WritePropertyName("csi");
            JsonSerializer.Serialize(writer, value.Csi, options);
        }

        if (value.Imu is not null)
        {
            writer.WritePropertyName("imu");
            JsonSerializer.Serialize(writer, value.Imu, options);
        }

        writer.WriteEndObject();
    }
}
