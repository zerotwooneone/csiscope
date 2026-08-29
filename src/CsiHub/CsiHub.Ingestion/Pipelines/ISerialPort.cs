using System.IO;

namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// Abstracts a physical or virtual serial port so <see cref="SerialPipelineReader"/>
/// can be integration-tested with in-memory streams without a real COM device.
/// </summary>
public interface ISerialPort : IDisposable
{
    /// <summary>
    /// The port identifier used for logging and diagnostics.
    /// </summary>
    string PortName { get; }

    /// <summary>
    /// Whether the port is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// The underlying duplex stream for reading and writing NDJSON.
    /// </summary>
    Stream BaseStream { get; }

    /// <summary>
    /// Opens the port for reading and writing.
    /// </summary>
    void Open();

    /// <summary>
    /// Closes the port to unblock any pending read or write operations.
    /// </summary>
    void Close();
}
