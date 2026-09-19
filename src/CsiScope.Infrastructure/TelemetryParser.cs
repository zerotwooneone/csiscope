using System.Text.Json;
using CsiScope.Domain.Model;

namespace CsiScope.Infrastructure;

public enum TelemetryKind : byte
{
    Csi,
    Ack,
    Config,
}

/// <summary>Decoded <c>{"type":"ack"}</c> frame — seq matches the outbound command.</summary>
public readonly record struct CommandAck(long Seq, bool Success);

/// <summary>
/// One decoded NDJSON line. <see cref="Kind"/> selects the meaningful field:
/// <see cref="Sample"/> for csi, <see cref="Ack"/> for ack,
/// <see cref="AnnouncedMac"/> for config.
/// </summary>
public readonly record struct ParsedTelemetry
{
    public TelemetryKind Kind { get; init; }
    public AmplitudeSample Sample { get; init; }
    public CommandAck Ack { get; init; }
    public MacAddress AnnouncedMac { get; init; }
}

/// <summary>
/// Zero-allocation NDJSON parser over <see cref="Utf8JsonReader"/>. Single
/// pass per line; the I/Q array is reduced to a mean magnitude inline so no
/// heap array is ever materialized. Malformed or torn input returns false —
/// never throws.
/// </summary>
public static class TelemetryParser
{
    public static bool TryParse(ReadOnlySpan<byte> line, DateTimeOffset now, out ParsedTelemetry result)
    {
        result = default;

        TelemetryKind? kind = null;
        MacAddress mac = default, src = default;
        WifiChannel channel = default;
        var rssi = 0;
        long seq = 0;
        var success = false;
        var hasMac = false;
        var hasSrc = false;
        var hasChannel = false;
        var hasRssi = false;
        var hasSeq = false;
        var hasSuccess = false;

        // I/Q reduction state — mean of sqrt(i^2 + q^2) over pairs.
        double magnitudeSum = 0;
        long iqPairs = 0;
        int pendingI = 0;
        var haveI = false;

        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("type"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    if (reader.ValueTextEquals("csi"u8)) kind = TelemetryKind.Csi;
                    else if (reader.ValueTextEquals("ack"u8)) kind = TelemetryKind.Ack;
                    else if (reader.ValueTextEquals("config"u8)) kind = TelemetryKind.Config;
                    else return false; // hb, imu, diag — not our concern
                }
                else if (reader.ValueTextEquals("mac"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    hasMac = MacAddress.TryParse(reader.ValueSpan, out mac);
                }
                else if (reader.ValueTextEquals("src"u8))
                {
                    if (!reader.Read() || !reader.TryGetUInt64(out var packed))
                    {
                        return false;
                    }

                    src = MacAddress.FromUInt64(packed);
                    hasSrc = true;
                }
                else if (reader.ValueTextEquals("ch"u8))
                {
                    if (!reader.Read() || !reader.TryGetInt32(out var ch))
                    {
                        return false;
                    }

                    channel = new WifiChannel(ch);
                    hasChannel = true;
                }
                else if (reader.ValueTextEquals("rssi"u8))
                {
                    if (!reader.Read() || !reader.TryGetInt32(out rssi))
                    {
                        return false;
                    }

                    hasRssi = true;
                }
                else if (reader.ValueTextEquals("seq"u8))
                {
                    if (!reader.Read() || !reader.TryGetInt64(out seq))
                    {
                        return false;
                    }

                    hasSeq = true;
                }
                else if (reader.ValueTextEquals("success"u8))
                {
                    if (!reader.Read() || reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                    {
                        return false;
                    }

                    success = reader.TokenType == JsonTokenType.True;
                    hasSuccess = true;
                }
                else if (reader.ValueTextEquals("c"u8))
                {
                    // Stream the I/Q array — reduce to mean magnitude inline.
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v))
                        {
                            if (!haveI)
                            {
                                pendingI = v;
                                haveI = true;
                            }
                            else
                            {
                                magnitudeSum += Math.Sqrt((pendingI * pendingI) + (v * v));
                                iqPairs++;
                                haveI = false;
                            }
                        }
                    }
                }
                else
                {
                    reader.Skip(); // t, bw, nz, blen, fwi, state, baud, version, cmd, dwell_ms, ...
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        switch (kind)
        {
            case TelemetryKind.Csi
                when hasMac && hasSrc && hasChannel && hasRssi && iqPairs > 0
                     && rssi is >= -127 and <= 0:
                result = new ParsedTelemetry
                {
                    Kind = TelemetryKind.Csi,
                    Sample = new AmplitudeSample(
                        new LinkIdentity(mac, src, channel),
                        now,
                        magnitudeSum / iqPairs,
                        new Rssi((short)rssi)),
                };
                return true;

            case TelemetryKind.Ack when hasSeq && hasSuccess:
                result = new ParsedTelemetry
                {
                    Kind = TelemetryKind.Ack,
                    Ack = new CommandAck(seq, success),
                };
                return true;

            case TelemetryKind.Config when hasMac:
                result = new ParsedTelemetry
                {
                    Kind = TelemetryKind.Config,
                    AnnouncedMac = mac,
                };
                return true;

            default:
                return false;
        }
    }
}
