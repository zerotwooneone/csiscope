using System.Net;
using System.Net.Sockets;
using CsiHub.Ingestion.Pipelines;

namespace CsiHub.Ingestion.IntegrationTests.Fakes;

/// <summary>
/// In-memory <see cref="ISerialPort"/> backed by a loopback TCP socket. The test writes
/// NDJSON into <see cref="Downlink"/> and reads responses from <see cref="Uplink"/>.
/// Because a single socket is duplex, both properties return the same client stream.
/// </summary>
public sealed class FakeSerialPort : ISerialPort
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private TcpClient? _server;

    public string PortName { get; }

    public bool IsOpen => _server?.Connected == true;

    public Stream BaseStream { get; private set; } = Stream.Null;

    /// <summary>
    /// Client-side stream used by the test to write NDJSON to the reader.
    /// </summary>
    public Stream Downlink { get; private set; } = Stream.Null;

    /// <summary>
    /// Client-side stream used by the test to read NDJSON from the reader.
    /// This is the same instance as <see cref="Downlink"/>.
    /// </summary>
    public Stream Uplink => Downlink;

    public FakeSerialPort(string portName)
    {
        PortName = portName;
    }

    public void Open()
    {
        if (IsOpen)
        {
            throw new InvalidOperationException("Port is already open.");
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        _client = new TcpClient();
        _client.Connect((IPEndPoint)_listener.LocalEndpoint);

        _server = _listener.AcceptTcpClient();

        _client.ReceiveBufferSize = 4096;
        _client.SendBufferSize = 4096;
        _client.NoDelay = true;

        _server.ReceiveBufferSize = 4096;
        _server.SendBufferSize = 4096;
        _server.NoDelay = true;

        BaseStream = _server.GetStream();
        Downlink = _client.GetStream();

        _listener.Stop();
    }

    public void Close()
    {
        try { BaseStream?.Close(); } catch { }
        try { _server?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }

        BaseStream = Stream.Null;
        Downlink = Stream.Null;
    }

    public void Dispose() => Close();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
