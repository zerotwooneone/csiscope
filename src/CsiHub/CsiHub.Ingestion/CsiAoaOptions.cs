using System;
using System.Collections.Generic;
using CsiHub.Core;

namespace CsiHub.Ingestion;

/// <summary>
/// Configuration for the distributed MUSIC AoA estimator.
/// </summary>
public sealed class CsiAoaOptions
{
    /// <summary>
    /// Node MAC (as reported in payloads) to physical sensor position in meters.
    /// </summary>
    public Dictionary<string, AoaEstimator.SensorPosition> SensorPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Carrier frequency used to compute wavelength. Default is 2.4 GHz.
    /// </summary>
    public double CarrierFrequencyHz { get; set; } = 2.4e9;

    /// <summary>
    /// Speed of light used with <see cref="CarrierFrequencyHz"/>.
    /// </summary>
    public double SpeedOfLight { get; set; } = 3.0e8;

    /// <summary>
    /// Number of expected signal sources for the MUSIC signal/noise subspace split.
    /// </summary>
    public int SourceCount { get; set; } = 1;

    /// <summary>
    /// Angular search grid step in degrees.
    /// </summary>
    public double StepDegrees { get; set; } = 1.0;

    /// <summary>
    /// Subcarrier index to use for the narrow-band AoA snapshot (0 = first subcarrier).
    /// </summary>
    public int SubcarrierIndex { get; set; } = 0;

    /// <summary>
    /// Maximum age of a per-node sample before it is considered stale and ignored.
    /// </summary>
    public TimeSpan SampleMaxAge { get; set; } = TimeSpan.FromSeconds(2.0);

    /// <summary>
    /// Multiplier applied to the 95th-percentile variance floor to determine
    /// when a RoomBaseline is considered converged. Default is 1.5.
    /// </summary>
    public double ConvergenceVarianceMultiplier { get; set; } = 1.5;
}
