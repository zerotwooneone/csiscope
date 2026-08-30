namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Production <see cref="ISerialPortFactory"/> that opens real <see cref="System.IO.Ports.SerialPort"/> devices.
/// </summary>
public sealed class SerialPortAdapterFactory : ISerialPortFactory
{
    public ISerialPort Create(string portName, int baudRate)
        => new SerialPortAdapter(portName, baudRate);
}
