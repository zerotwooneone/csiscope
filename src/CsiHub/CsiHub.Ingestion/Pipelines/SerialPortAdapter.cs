using System.IO.Ports;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Production adapter that wraps <see cref="System.IO.Ports.SerialPort"/>.
/// </summary>
public sealed class SerialPortAdapter : ISerialPort
{
    private readonly SerialPort _port;

    public string PortName => _port.PortName;

    public bool IsOpen => _port.IsOpen;

    public Stream BaseStream => _port.BaseStream;

    public SerialPortAdapter(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = -1,
            WriteTimeout = -1
        };
    }

    public void Open() => _port.Open();

    public void Close()
    {
        if (_port.IsOpen)
        {
            _port.Close();
        }
    }

    public void Dispose() => _port.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
