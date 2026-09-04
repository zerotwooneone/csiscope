using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Length-prefixed framed NDJSON protocol helpers. Mirrors the firmware side.
/// Frame layout: [0xA5][0x5A][length: uint16_t LE][payload: length bytes][crc32: uint32_t LE].
/// CRC-32 (IEEE 802.3) is computed over [length bytes][payload bytes].
/// </summary>
internal static class SerialFraming
{
    public const byte MagicHigh = 0xA5;
    public const byte MagicLow = 0x5A;
    public const int HeaderSize = 2 + sizeof(ushort);
    public const int CrcSize = sizeof(uint);

    private const int MaxPayloadLength = 8192;

    private static readonly byte[] s_magic = { MagicHigh, MagicLow };

    /// <summary>
    /// Tries to read one valid frame from the supplied buffer. If a frame is found,
    /// the buffer is advanced past it and the payload is returned. Invalid bytes are
    /// dropped to resync. On partial data, the buffer is left unchanged at the start of
    /// the best candidate so the caller can wait for more bytes.
    /// </summary>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> payload)
    {
        payload = default;
        var reader = new SequenceReader<byte>(buffer);

        while (!reader.End)
        {
            // Discard anything up to the magic bytes. Keep the final byte so an
            // incomplete magic pair at the end of the buffer is not lost.
            if (!reader.TryReadTo(out ReadOnlySpan<byte> _, s_magic, advancePastDelimiter: false))
            {
                if (reader.Consumed > 0)
                {
                    reader.Rewind(1);
                    buffer = buffer.Slice(reader.Position);
                }

                return false;
            }

            // reader is now positioned at the first magic byte. We need at least
            // the magic and the 2-byte length to continue.
            if (reader.Remaining < HeaderSize)
            {
                buffer = buffer.Slice(reader.Position);
                return false;
            }

            // Peek the header without advancing so we can back out of a partial frame.
            if (!reader.TryPeek(2, out byte lenLow) || !reader.TryPeek(3, out byte lenHigh))
            {
                buffer = buffer.Slice(reader.Position);
                return false;
            }

            ushort payloadLen = (ushort)(lenLow | (lenHigh << 8));

            if (payloadLen == 0 || payloadLen > MaxPayloadLength)
            {
                // Drop the first magic byte and resync.
                reader.Advance(1);
                continue;
            }

            int totalFrameSize = HeaderSize + payloadLen + CrcSize;
            if (reader.Remaining < totalFrameSize)
            {
                // Full frame not yet in the buffer; keep from the magic onward.
                buffer = buffer.Slice(reader.Position);
                return false;
            }

            // Slice the frame, payload and CRC from the underlying sequence. The
            // SequenceReader position is still at the first magic byte.
            var frame = buffer.Slice(reader.Position, totalFrameSize);
            var payloadSeq = frame.Slice(HeaderSize, payloadLen);
            var crcSeq = frame.Slice(HeaderSize + payloadLen, CrcSize);

            uint expectedCrc = ReadCrc32(crcSeq);
            uint actualCrc = ComputeCrc(payloadLen, payloadSeq);

            if (actualCrc != expectedCrc)
            {
                // Corrupt or false-positive magic; drop the first magic and resync.
                reader.Advance(1);
                continue;
            }

            // Valid frame. Advance past it, trim the input buffer, and return the payload.
            reader.Advance(totalFrameSize);
            buffer = buffer.Slice(reader.Position);
            payload = payloadSeq;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a framed byte array for the supplied UTF-8 encoded JSON payload.
    /// </summary>
    public static byte[] CreateFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Payload is too large for a 16-bit length field.", nameof(payload));
        }

        ushort payloadLen = (ushort)payload.Length;
        byte[] frame = new byte[HeaderSize + payloadLen + CrcSize];

        frame[0] = MagicHigh;
        frame[1] = MagicLow;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), payloadLen);
        payload.CopyTo(frame.AsSpan(HeaderSize));

        uint crc = ComputeCrc(payloadLen, payload);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(HeaderSize + payloadLen), crc);

        return frame;
    }

    /// <summary>
    /// Convenience helper that creates a frame from a JSON string.
    /// </summary>
    public static byte[] CreateFrame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        return CreateFrame(payload);
    }

    /// <summary>
    /// Computes the IEEE 802.3 CRC-32 over [length bytes (LE)][payload].
    /// </summary>
    public static uint ComputeCrc(ushort payloadLength, ReadOnlySequence<byte> payload)
    {
        Span<byte> lengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, payloadLength);

        var crc = new Crc32();
        crc.Append(lengthBytes);

        foreach (var segment in payload)
        {
            crc.Append(segment.Span);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Computes the IEEE 802.3 CRC-32 over [length bytes (LE)][payload].
    /// </summary>
    public static uint ComputeCrc(ushort payloadLength, ReadOnlySpan<byte> payload)
    {
        Span<byte> lengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, payloadLength);

        var crc = new Crc32();
        crc.Append(lengthBytes);
        crc.Append(payload);

        return crc.GetCurrentHashAsUInt32();
    }

    private static uint ReadCrc32(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(sequence.First.Span);
        }

        Span<byte> bytes = stackalloc byte[4];
        sequence.CopyTo(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
