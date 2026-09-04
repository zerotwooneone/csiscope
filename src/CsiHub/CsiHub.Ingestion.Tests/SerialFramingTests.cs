using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using CsiHub.Ingestion.Pipelines;

namespace CsiHub.Ingestion.Tests;

public class SerialFramingTests
{
    private static readonly byte[] s_magic = { SerialFraming.MagicHigh, SerialFraming.MagicLow };

    [Fact]
    public void CreateFrame_ContainsMagicLengthCrc()
    {
        const string json = """{"type":"hb"}""";
        byte[] payload = Encoding.UTF8.GetBytes(json);

        byte[] frame = SerialFraming.CreateFrame(payload);

        Assert.Equal(SerialFraming.MagicHigh, frame[0]);
        Assert.Equal(SerialFraming.MagicLow, frame[1]);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));
        Assert.Equal((ushort)payload.Length, length);
    }

    [Fact]
    public void TryReadFrame_RoundTripsPayload()
    {
        const string json = """{"type":"csi","mac":"AA:BB:CC:DD:EE:FF"}""";
        byte[] frame = SerialFraming.CreateFrame(Encoding.UTF8.GetBytes(json));
        var buffer = new ReadOnlySequence<byte>(frame);

        bool result = SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload);

        Assert.True(result);
        string readJson = Encoding.UTF8.GetString(payload.ToArray());
        Assert.Equal(json, readJson);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void TryReadFrame_DetectsCorruptPayload()
    {
        const string json = """{"type":"hb"}""";
        byte[] frame = SerialFraming.CreateFrame(Encoding.UTF8.GetBytes(json));
        frame[^1] ^= 0xFF; // Corrupt the last CRC byte.
        var buffer = new ReadOnlySequence<byte>(frame);

        bool result = SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload);

        Assert.False(result);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void TryReadFrame_HandlesPartialFrame()
    {
        const string json = """{"type":"hb"}""";
        byte[] frame = SerialFraming.CreateFrame(Encoding.UTF8.GetBytes(json));
        var buffer = new ReadOnlySequence<byte>(frame.AsSpan(0, frame.Length - 3).ToArray());

        bool result = SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload);

        Assert.False(result);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void TryReadFrame_ResyncsAfterNoise()
    {
        const string json = """{"type":"hb"}""";
        byte[] frame = SerialFraming.CreateFrame(Encoding.UTF8.GetBytes(json));
        byte[] noise = Encoding.UTF8.GetBytes("some random text {");
        byte[] combined = new byte[noise.Length + frame.Length];
        noise.CopyTo(combined, 0);
        frame.CopyTo(combined, noise.Length);
        var buffer = new ReadOnlySequence<byte>(combined);

        bool result = SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload);

        Assert.True(result);
        string readJson = Encoding.UTF8.GetString(payload.ToArray());
        Assert.Equal(json, readJson);
    }

    [Fact]
    public void TryReadFrame_ResyncsAfterFalseMagic()
    {
        const string json = """{"type":"hb"}""";
        byte[] frame = SerialFraming.CreateFrame(Encoding.UTF8.GetBytes(json));

        // Insert a false magic pair with an invalid following length field.
        byte[] falseMagic = { SerialFraming.MagicHigh, SerialFraming.MagicLow, 0x00, 0xFF };
        byte[] combined = new byte[falseMagic.Length + frame.Length];
        falseMagic.CopyTo(combined, 0);
        frame.CopyTo(combined, falseMagic.Length);
        var buffer = new ReadOnlySequence<byte>(combined);

        bool result = SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload);

        Assert.True(result);
        string readJson = Encoding.UTF8.GetString(payload.ToArray());
        Assert.Equal(json, readJson);
    }

    [Fact]
    public void TryReadFrame_HandlesMultiSegmentBuffer()
    {
        const string json = """{"type":"hb","mac":"AA:BB:CC:DD:EE:FF"}""";
        byte[] frame = SerialFraming.CreateFrame(Encoding.UTF8.GetBytes(json));

        // Split the frame across two segments.
        int split = frame.Length / 2;
        var first = new ReadOnlyMemory<byte>(frame, 0, split);
        var second = new ReadOnlyMemory<byte>(frame, split, frame.Length - split);
        var segment = new TestBufferSegment(first, second);
        var buffer = new ReadOnlySequence<byte>(segment, 0, segment.Next!, second.Length);

        bool result = SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload);

        Assert.True(result);
        string readJson = Encoding.UTF8.GetString(payload.ToArray());
        Assert.Equal(json, readJson);
        Assert.True(buffer.IsEmpty);
    }

    private sealed class TestBufferSegment : ReadOnlySequenceSegment<byte>
    {
        public TestBufferSegment(ReadOnlyMemory<byte> memory, ReadOnlyMemory<byte> next)
        {
            Memory = memory;
            var nextSegment = new TestBufferSegment(next);
            Next = nextSegment;
            RunningIndex = 0;
            nextSegment.RunningIndex = memory.Length;
        }

        private TestBufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }
    }
}
