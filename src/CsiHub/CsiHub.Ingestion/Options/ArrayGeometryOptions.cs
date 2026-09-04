using System;

namespace CsiHub.Ingestion;

/// <summary>
/// User-facing geometry assignments for a three-node L-shaped array.
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

    /// <summary>
    /// Maximum age before a per-node CSI sample is ignored for AoA.
    /// </summary>
    public TimeSpan SampleMaxAge { get; set; } = TimeSpan.FromSeconds(2.0);

    /// <summary>
    /// Carrier frequency used to compute wavelength. Default 2.4 GHz.
    /// </summary>
    public double CarrierFrequencyHz { get; set; } = 2.4e9;

    /// <summary>
    /// Speed of light used with <see cref="CarrierFrequencyHz"/>.
    /// </summary>
    public double SpeedOfLight { get; set; } = 3.0e8;

    /// <summary>
    /// MUSIC search step in degrees.
    /// </summary>
    public double StepDegrees { get; set; } = 1.0;

    /// <summary>
    /// Subcarrier index used for the AoA snapshot.
    /// </summary>
    public int SubcarrierIndex { get; set; } = 0;

    /// <summary>
    /// Number of expected signal sources for MUSIC.
    /// </summary>
    public int SourceCount { get; set; } = 1;
}
