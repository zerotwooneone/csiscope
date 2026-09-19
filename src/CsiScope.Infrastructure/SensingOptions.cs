namespace CsiScope.Infrastructure;

/// <summary>
/// Configuration for the sensing host — bound from the "CsiScope" section.
/// Property names mirror the legacy CsiIngestion schema where they overlap.
/// </summary>
public sealed class SensingOptions
{
    /// <summary>Serial ports to open — one ESP32-S3 node per port.</summary>
    public IList<string> SerialPortNames { get; set; } = new List<string>();

    /// <summary>Baud rate for all nodes — ESP32-S3 CDC rate.</summary>
    public int SerialBaudRate { get; set; } = 921600;

    /// <summary>Delay between reconnect attempts after a port drops.</summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>Per-attempt ACK wait for a set_rf command.</summary>
    public int AckTimeoutMs { get; set; } = 1000;

    /// <summary>Total transmission attempts (including the first) per command.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Orchestration tick interval.</summary>
    public int TickIntervalMs { get; set; } = 100;
}
