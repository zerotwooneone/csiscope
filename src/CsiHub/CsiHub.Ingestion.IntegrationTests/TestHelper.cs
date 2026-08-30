using System.Text;
using CsiHub.Ingestion.IntegrationTests.Fakes;

namespace CsiHub.Ingestion.IntegrationTests;

public static class TestHelper
{
    public static async Task WriteLineAsync(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public static async Task WaitForOpenAsync(FakeSerialPort port)
    {
        while (!port.IsOpen)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
