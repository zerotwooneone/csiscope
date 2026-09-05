using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace CsiHub.Core.Tests;

public class AoaEstimatorTests
{
    [Fact]
    public void Estimate_Resolves_Source_At_30_Degrees_For_Four_Element_Ula()
    {
        // 4-element uniform linear array along the X axis, half-wavelength spacing.
        const double frequencyHz = 2.4e9;
        const double speedOfLight = 3e8;
        double wavelength = speedOfLight / frequencyHz;
        double spacing = wavelength / 2.0;

        var sensors = new List<AoaEstimator.SensorPosition>
        {
            new(0.0, 0.0),
            new(spacing, 0.0),
            new(2.0 * spacing, 0.0),
            new(3.0 * spacing, 0.0),
        };

        const double sourceAngleDegrees = 30.0;
        double sourceAngleRad = sourceAngleDegrees * Math.PI / 180.0;

        // Generate 50 noisy snapshots for a single source.
        var snapshots = new List<Complex[]>();
        var rng = new Random(12345);
        for (int s = 0; s < 50; s++)
        {
            var snapshot = new Complex[sensors.Count];
            for (int i = 0; i < sensors.Count; i++)
            {
                double delay = (sensors[i].X * Math.Sin(sourceAngleRad) + sensors[i].Y * Math.Cos(sourceAngleRad)) / wavelength;
                double phase = -2.0 * Math.PI * delay;
                snapshot[i] = Complex.Exp(new Complex(0.0, phase));

                // Add a small amount of complex Gaussian-ish noise.
                double noiseStd = 0.05;
                snapshot[i] = new Complex(
                    snapshot[i].Real + noiseStd * BoxMuller(rng),
                    snapshot[i].Imaginary + noiseStd * BoxMuller(rng));
            }

            snapshots.Add(snapshot);
        }

        var result = AoaEstimator.Estimate(sensors, snapshots, wavelength, sourceCount: 1, stepDegrees: 1.0);

        Assert.NotNull(result);
        Assert.NotNull(result!.PeakAngleDegrees);
        Assert.Equal(sourceAngleDegrees, result.PeakAngleDegrees!.Value, precision: 2);
    }

    [Fact]
    public void Estimate_Resolves_Source_For_L_Shaped_Array()
    {
        // Mirrors the deployed geometry: three nodes in an L with half-wavelength arms.
        const double wavelength = 0.125; // 2.4 GHz
        var sensors = LShapedArray(wavelength / 2.0);

        const double sourceAngleDegrees = 30.0;
        var rng = new Random(6789);
        var snapshots = Enumerable.Range(0, 50)
            .Select(_ => BuildSnapshot(sensors, sourceAngleDegrees, wavelength, noiseStd: 0.05, rng: rng))
            .ToList();

        var result = AoaEstimator.Estimate(sensors, snapshots, wavelength, sourceCount: 1, stepDegrees: 1.0);

        Assert.NotNull(result);
        Assert.NotNull(result!.PeakAngleDegrees);
        Assert.InRange(result.PeakAngleDegrees!.Value, sourceAngleDegrees - 2.0, sourceAngleDegrees + 2.0);
    }

    [Fact]
    public void Estimate_Single_Snapshot_Resolves_Source()
    {
        // The production path feeds exactly one snapshot per estimate
        // (CsiDspBackgroundService.TryUpdateAoa), so this path must work.
        const double wavelength = 0.125;
        var sensors = LShapedArray(wavelength / 2.0);
        const double sourceAngleDegrees = -25.0;

        var snapshots = new[] { BuildSnapshot(sensors, sourceAngleDegrees, wavelength) };

        var result = AoaEstimator.Estimate(sensors, snapshots, wavelength, sourceCount: 1, stepDegrees: 1.0);

        Assert.NotNull(result);
        Assert.InRange(result!.PeakAngleDegrees!.Value, sourceAngleDegrees - 1.5, sourceAngleDegrees + 1.5);
    }

    [Theory]
    [InlineData(-45.0)]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void Estimate_Recovers_Distinct_Peaks_For_Distinct_Sources(double sourceAngleDegrees)
    {
        // Guards the "one angle for all MACs" symptom: with clean phases the
        // estimator must produce different peaks for different true bearings.
        const double wavelength = 0.125;
        var sensors = LShapedArray(wavelength / 2.0);

        var snapshots = new[] { BuildSnapshot(sensors, sourceAngleDegrees, wavelength) };

        var result = AoaEstimator.Estimate(sensors, snapshots, wavelength, sourceCount: 1, stepDegrees: 1.0);

        Assert.NotNull(result);
        Assert.InRange(result!.PeakAngleDegrees!.Value, sourceAngleDegrees - 1.5, sourceAngleDegrees + 1.5);
    }

    [Fact]
    public void Estimate_Array_Rotation_Shifts_Peak_Without_Config_Change()
    {
        // Rotating the physical array while the transmitter stays fixed changes
        // the source's bearing in the array frame, so the estimate must move
        // even though the configured sensor positions never change. No IMU or
        // other orientation input is required for the angle itself to shift.
        const double wavelength = 0.125;
        var bodySensors = LShapedArray(wavelength / 2.0);

        const double worldSourceDegrees = 40.0;
        const double rotationDegrees = 25.0;

        // Baseline: sensor world positions equal the configured body positions.
        var baselineSnapshots = new[] { BuildSnapshot(bodySensors, worldSourceDegrees, wavelength) };
        var baseline = AoaEstimator.Estimate(bodySensors, baselineSnapshots, wavelength, 1, 1.0);

        // Physically rotate the array: the sensors' world positions rotate, so
        // the measured per-sensor phases change. The estimator is still given
        // the unmodified configured positions, exactly as production does.
        var rotatedWorldSensors = bodySensors.Select(p => Rotate(p, rotationDegrees)).ToList();
        var rotatedSnapshots = new[] { BuildSnapshot(rotatedWorldSensors, worldSourceDegrees, wavelength) };
        var rotated = AoaEstimator.Estimate(bodySensors, rotatedSnapshots, wavelength, 1, 1.0);

        Assert.NotNull(baseline);
        Assert.NotNull(rotated);
        Assert.InRange(baseline!.PeakAngleDegrees!.Value, worldSourceDegrees - 1.5, worldSourceDegrees + 1.5);
        // Rotating the sensor world positions by +25° makes the fixed world
        // source appear at worldSourceDegrees + rotationDegrees in the array frame.
        Assert.InRange(rotated!.PeakAngleDegrees!.Value,
            worldSourceDegrees + rotationDegrees - 1.5,
            worldSourceDegrees + rotationDegrees + 1.5);
    }

    [Fact]
    public void Estimate_Uncalibrated_Phase_Offsets_Corrupt_Peak()
    {
        // Each ESP32 contributes an unknown constant phase offset (independent
        // PLL and packet-detection timing). With no calibration these offsets
        // dominate the measured phase vector and pull the estimate far off the
        // true bearing — the mechanism behind the stable wrong angle observed
        // on live hardware.
        const double wavelength = 0.125;
        var sensors = LShapedArray(wavelength / 2.0);
        double[] offsetsRad = { 0.0, 2.1, -1.4 };

        const double trueAngle = -30.0;
        double peak = EstimatePeak(sensors, trueAngle, wavelength, offsetsRad);

        Assert.True(Math.Abs(peak - trueAngle) > 5.0,
            $"Expected fixed offsets to corrupt the estimate, but peak {peak}° is near truth {trueAngle}°");
    }

    [Fact]
    public void Estimate_All_Zero_Samples_Returns_Degenerate_Result()
    {
        // A dead subcarrier (e.g. index 0, the DC/null tone) yields all-zero
        // samples. The estimator cannot distinguish this from real data: it
        // still returns a spectrum and a peak, so flat-lined input produces a
        // stable, meaningless angle.
        const double wavelength = 0.125;
        var sensors = LShapedArray(wavelength / 2.0);
        var snapshots = new[] { new Complex[sensors.Count] };

        var result = AoaEstimator.Estimate(sensors, snapshots, wavelength, 1, 1.0);

        Assert.NotNull(result);
        Assert.NotNull(result!.PeakAngleDegrees);
        Assert.All(result.Spectrum, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void Estimate_Rejects_Invalid_Inputs()
    {
        const double wavelength = 0.125;
        var sensors = LShapedArray(wavelength / 2.0);
        var good = new[] { BuildSnapshot(sensors, 10.0, wavelength) };

        Assert.Null(AoaEstimator.Estimate(null!, good, wavelength));
        Assert.Null(AoaEstimator.Estimate(sensors, null!, wavelength));
        Assert.Null(AoaEstimator.Estimate(sensors.Take(1).ToList(), new[] { new Complex[1] }, wavelength));
        Assert.Null(AoaEstimator.Estimate(sensors, Array.Empty<Complex[]>(), wavelength));
        Assert.Null(AoaEstimator.Estimate(sensors, new[] { new Complex[2] }, wavelength));
        Assert.Null(AoaEstimator.Estimate(sensors, good, 0.0));
        Assert.Null(AoaEstimator.Estimate(sensors, good, -1.0));
        Assert.Null(AoaEstimator.Estimate(sensors, good, wavelength, sourceCount: 0));
        Assert.Null(AoaEstimator.Estimate(sensors, good, wavelength, sourceCount: 3));
        Assert.Null(AoaEstimator.Estimate(sensors, good, wavelength, stepDegrees: 0.0));
    }

    private static List<AoaEstimator.SensorPosition> LShapedArray(double spacing) =>
        new()
        {
            new AoaEstimator.SensorPosition(0.0, 0.0),
            new AoaEstimator.SensorPosition(spacing, 0.0),
            new AoaEstimator.SensorPosition(0.0, spacing),
        };

    private static AoaEstimator.SensorPosition Rotate(AoaEstimator.SensorPosition p, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return new AoaEstimator.SensorPosition(
            (p.X * cos) - (p.Y * sin),
            (p.X * sin) + (p.Y * cos),
            p.Z);
    }

    private static Complex[] BuildSnapshot(
        IReadOnlyList<AoaEstimator.SensorPosition> sensors,
        double sourceAngleDegrees,
        double wavelength,
        double[]? phaseOffsetsRad = null,
        double noiseStd = 0.0,
        Random? rng = null)
    {
        double rad = sourceAngleDegrees * Math.PI / 180.0;
        double ux = Math.Sin(rad);
        double uy = Math.Cos(rad);

        var snapshot = new Complex[sensors.Count];
        for (int i = 0; i < sensors.Count; i++)
        {
            double delay = ((sensors[i].X * ux) + (sensors[i].Y * uy)) / wavelength;
            double phase = (-2.0 * Math.PI * delay) + (phaseOffsetsRad?[i] ?? 0.0);
            var sample = Complex.Exp(new Complex(0.0, phase));
            if (noiseStd > 0.0 && rng is not null)
            {
                sample = new Complex(
                    sample.Real + (noiseStd * BoxMuller(rng)),
                    sample.Imaginary + (noiseStd * BoxMuller(rng)));
            }

            snapshot[i] = sample;
        }

        return snapshot;
    }

    private static double EstimatePeak(
        IReadOnlyList<AoaEstimator.SensorPosition> sensors,
        double sourceAngleDegrees,
        double wavelength,
        double[]? phaseOffsetsRad = null)
    {
        var snapshots = new[] { BuildSnapshot(sensors, sourceAngleDegrees, wavelength, phaseOffsetsRad) };
        var result = AoaEstimator.Estimate(sensors, snapshots, wavelength, sourceCount: 1, stepDegrees: 1.0);
        Assert.NotNull(result);
        Assert.NotNull(result!.PeakAngleDegrees);
        return result.PeakAngleDegrees!.Value;
    }

    private static double BoxMuller(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
