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

    private static double BoxMuller(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
