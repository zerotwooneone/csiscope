using System.Net;
using System.Net.Sockets;

namespace CsiScope.Infrastructure.IntegrationTests;

/// <summary>
/// In-memory serial-port stand-in backed by a loopback TCP socket — the same
/// trick as the legacy FakeSerialPort, adapted to the worker's Stream factory.
/// The worker reads/writes <see cref="DeviceSide"/>; the test drives the node
/// over <see cref="HostSide"/> (write NDJSON in, read commands out).
/// </summary>
public sealed class LoopbackSerialStream : IAsyncDisposable
{
    private readonly TcpClient _host;
    private readonly TcpClient _device;
    private readonly NetworkStream _deviceStream;
    private readonly NetworkStream _hostStream;

    public LoopbackSerialStream()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        _host = new TcpClient();
        _host.Connect((IPEndPoint)listener.LocalEndpoint);
        _device = listener.AcceptTcpClient();
        listener.Stop();

        _host.NoDelay = true;
        _device.NoDelay = true;

        // Cache the streams — GetStream() throws once the socket is closed,
        // which the worker's cancel-registration teardown does on shutdown.
        _deviceStream = _device.GetStream();
        _hostStream = _host.GetStream();
    }

    /// <summary>Worker side — handed to the openStream factory.</summary>
    public Stream DeviceSide => _deviceStream;

    /// <summary>Test side — write node NDJSON here, read host commands here.</summary>
    public Stream HostSide => _hostStream;

    public async ValueTask DisposeAsync()
    {
        await _deviceStream.DisposeAsync();
        await _hostStream.DisposeAsync();
        _device.Dispose();
        _host.Dispose();
    }
}
