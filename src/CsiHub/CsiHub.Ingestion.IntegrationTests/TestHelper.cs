using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using CsiHub.Ingestion.IntegrationTests.Fakes;
using CsiHub.Ingestion.Pipelines;

namespace CsiHub.Ingestion.IntegrationTests;

public static class TestHelper
{
    public static async Task WriteFrameAsync(Stream stream, string text)
    {
        byte[] frame = SerialFraming.CreateFrame(text);
        await stream.WriteAsync(frame.AsMemory()).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[SerialFraming.HeaderSize];

        int read = await stream.ReadAtLeastAsync(header.AsMemory(), header.Length, true, cancellationToken).ConfigureAwait(false);
        if (read < header.Length)
        {
            return null;
        }

        if (header[0] != SerialFraming.MagicHigh || header[1] != SerialFraming.MagicLow)
        {
            return null;
        }

        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        byte[] frame = new byte[SerialFraming.HeaderSize + payloadLength + SerialFraming.CrcSize];
        header.CopyTo(frame, 0);

        int remaining = payloadLength + SerialFraming.CrcSize;
        int offset = SerialFraming.HeaderSize;

        while (remaining > 0)
        {
            int bytesRead = await stream.ReadAsync(frame.AsMemory(offset, remaining), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return null;
            }

            offset += bytesRead;
            remaining -= bytesRead;
        }

        var buffer = new ReadOnlySequence<byte>(frame);
        return SerialFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload)
            ? Encoding.UTF8.GetString(payload.ToArray())
            : null;
    }

    public static async Task WaitForOpenAsync(FakeSerialPort port)
    {
        while (!port.IsOpen)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
