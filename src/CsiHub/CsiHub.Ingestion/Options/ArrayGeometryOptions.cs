namespace CsiHub.Ingestion;

/// <summary>
/// User-facing geometry assignments for a three-node L-shaped array. This is the
/// single source of truth for the array layout; it is persisted to the user's
/// local application-data directory, and the sensor-position map used for AoA is
/// derived from it at runtime.
/// </summary>
public sealed class ArrayGeometryOptions
{
    /// <summary>
    /// MAC address of the node placed at the origin (0, 0).
    /// </summary>
    public string? OriginMac { get; set; }

    /// <summary>
    /// MAC address of the node placed on the +X arm.
    /// </summary>
    public string? XArmMac { get; set; }

    /// <summary>
    /// MAC address of the node placed on the +Y arm.
    /// </summary>
    public string? YArmMac { get; set; }

    /// <summary>
    /// Distance from the origin to the X-arm node in meters.
    /// </summary>
    public double XArmSpacingMeters { get; set; } = 0.0625;

    /// <summary>
    /// Distance from the origin to the Y-arm node in meters.
    /// </summary>
    public double YArmSpacingMeters { get; set; } = 0.0625;
}
