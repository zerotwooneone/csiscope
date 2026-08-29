namespace CsiHub.Ingestion;

/// <summary>
/// Configuration for the multi-node serial ingestion layer.
/// </summary>
public sealed class CsiIngestionOptions
{
    /// <summary>
    /// Serial ports that the host should open and ingest from.
    /// </summary>
    public IList<string> SerialPortNames { get; set; } = new List<string>();

    /// <summary>
    /// Baud rate for all array nodes. Defaults to the ESP32-S3 CDC rate.
    /// </summary>
    public int SerialBaudRate { get; set; } = 921600;

    /// <summary>
    /// Delay between reconnection attempts after a node drops off the bus.
    /// </summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>
    /// Maximum number of parsed CSI/IMU payloads to keep in the ingestion channel.
    /// Older payloads are dropped when the channel is full and the DSP pipeline is behind.
    /// </summary>
    public int PayloadChannelCapacity { get; set; } = 1000;

    /// <summary>
    /// Maximum number of node state-change events to keep in the state channel.
    /// </summary>
    public int StateChannelCapacity { get; set; } = 256;

    /// <summary>
    /// Maximum number of outbound NDJSON commands to queue per port.
    /// Older commands are dropped when the channel is full so stale commands
    /// are not sent to a reconnected node.
    /// </summary>
    public int CommandChannelCapacity { get; set; } = 32;
}
