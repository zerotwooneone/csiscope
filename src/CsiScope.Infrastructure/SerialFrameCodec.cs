using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CsiScope.Infrastructure;

/// <summary>Delivers one decoded frame payload.</summary>
public delegate void FrameHandler(ReadOnlySpan<byte> payload);

/// <summary>
/// The node wire protocol — binary-framed in BOTH directions:
/// <c>[0xA5][0x5A][len_lo][len_hi][payload][crc32_le]</c>, where CRC-32
/// (IEEE 802.3, reflected 0xEDB88320, init/xorout 0xFFFFFFFF) covers the two
/// length bytes plus the payload. The payload is JSON (csi frames append a
/// trailing '\n' inside the payload — harmless to the JSON parser).
/// </summary>
public static class SerialFrameCodec
{
    public const byte MagicHigh = 0xA5;
    public const byte MagicLow = 0x5A;
    public const int HeaderSize = 4;   // magic + len
    public const int TrailerSize = 4;  // crc32
    public const int MaxPayload = 4096; // firmware CsiJsonBufferSize ceiling

    private static readonly uint[] CrcTable = BuildTable();

    /// <summary>Wraps a JSON payload in the binary frame the firmware expects.</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length + TrailerSize];
        frame[0] = MagicHigh;
        frame[1] = MagicLow;
        frame[2] = (byte)(payload.Length & 0xFF);
        frame[3] = (byte)((payload.Length >> 8) & 0xFF);
        payload.CopyTo(frame.AsSpan(HeaderSize));

        // CRC covers the two length bytes + payload — same span the firmware CRCs.
        var crc = ~Crc32Update(0xFFFFFFFF, frame.AsSpan(2, 2 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(HeaderSize + payload.Length), crc);
        return frame;
    }

    /// <summary>
    /// Extracts every complete, CRC-valid frame from <paramref name="accum"/>,
    /// invoking <paramref name="onFrame"/> per payload, then removes the consumed
    /// bytes. Noise before a magic marker is skipped; a partial trailing frame is
    /// left in the accumulator for the next call.
    /// </summary>
    public static void DrainFrames(List<byte> accum, FrameHandler onFrame)
    {
        var span = CollectionsMarshal.AsSpan(accum);
        var i = 0;
        while (i + HeaderSize <= span.Length)
        {
            if (span[i] != MagicHigh || span[i + 1] != MagicLow)
            {
                i++;
                continue;
            }

            var len = span[i + 2] | (span[i + 3] << 8);
            if (len == 0 || len > MaxPayload)
            {
                i++; // bogus length — resync past the magic byte
                continue;
            }

            var total = HeaderSize + len + TrailerSize;
            if (i + total > span.Length)
            {
                break; // incomplete frame — wait for more bytes
            }

            var expected = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i + HeaderSize + len, TrailerSize));
            var actual = ~Crc32Update(0xFFFFFFFF, span.Slice(i + 2, 2 + len));
            if (actual == expected)
            {
                onFrame(span.Slice(i + HeaderSize, len));
                i += total;
            }
            else
            {
                i++; // corrupt frame — resync past the magic byte
            }
        }

        if (i > 0)
        {
            accum.RemoveRange(0, i);
        }
    }

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
