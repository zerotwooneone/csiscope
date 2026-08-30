namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Creates <see cref="ISerialPort"/> instances for a given logical port.
/// Abstracting this allows integration tests to substitute in-memory fakes
/// while the production host uses real COM ports.
/// </summary>
public interface ISerialPortFactory
{
    /// <summary>
    /// Creates an <see cref="ISerialPort"/> for the specified logical port and baud rate.
    /// </summary>
    ISerialPort Create(string portName, int baudRate);
}
