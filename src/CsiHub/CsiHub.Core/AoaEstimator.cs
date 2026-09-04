using System;
using System.Buffers;
using System.Linq;
using Complex = System.Numerics.Complex;
using MathNet.Numerics.LinearAlgebra;

namespace CsiHub.Core;

/// <summary>
/// Single-snapshot / narrow-band MUSIC AoA estimator using Hermitian EVD via MathNet.Numerics.
/// </summary>
public static class AoaEstimator
{
    /// <summary>
    /// Antenna / sensor position in meters.
    /// </summary>
    public readonly record struct SensorPosition(double X, double Y, double Z = 0.0);

    /// <summary>
    /// Result of a MUSIC pseudo-spectrum sweep.
    /// </summary>
    public sealed class AoaResult
    {
        public AoaResult(double[] candidateAngles, double[] spectrum)
        {
            CandidateAngles = candidateAngles ?? throw new ArgumentNullException(nameof(candidateAngles));
            Spectrum = spectrum ?? throw new ArgumentNullException(nameof(spectrum));

            if (CandidateAngles.Length != Spectrum.Length)
            {
                throw new ArgumentException("Candidate angle and spectrum arrays must have the same length.");
            }
        }

        /// <summary>
        /// Grid of azimuth angles in degrees, [-90, 90].
        /// </summary>
        public double[] CandidateAngles { get; }

        /// <summary>
        /// Normalized MUSIC pseudo-spectrum (linear, not dB).
        /// </summary>
        public double[] Spectrum { get; }

        /// <summary>
        /// Angle of the highest spectrum peak.
        /// </summary>
        public double? PeakAngleDegrees
        {
            get
            {
                if (Spectrum.Length == 0)
                {
                    return null;
                }

                int best = 0;
                for (int i = 1; i < Spectrum.Length; i++)
                {
                    if (Spectrum[i] > Spectrum[best])
                    {
                        best = i;
                    }
                }

                return CandidateAngles[best];
            }
        }
    }

    /// <summary>
    /// Estimates the azimuth angle of arrival using the MUSIC algorithm.
    /// </summary>
    /// <param name="sensors">Sensor positions in meters.</param>
    /// <param name="snapshots">
    /// One or more snapshots. Each snapshot is an array of complex baseband samples,
    /// one per sensor, in the same order as <paramref name="sensors"/>.
    /// </param>
    /// <param name="wavelength">Carrier wavelength in meters.</param>
    /// <param name="sourceCount">Number of expected signal sources.</param>
    /// <param name="stepDegrees">Angular search grid step in degrees.</param>
    public static AoaResult? Estimate(
        IReadOnlyList<SensorPosition> sensors,
        IReadOnlyList<Complex[]> snapshots,
        double wavelength,
        int sourceCount = 1,
        double stepDegrees = 1.0)
    {
        if (sensors is null || snapshots is null || sensors.Count < 2 || snapshots.Count == 0)
        {
            return null;
        }

        if (wavelength <= 0.0 || stepDegrees <= 0.0)
        {
            return null;
        }

        if (sourceCount < 1 || sourceCount >= sensors.Count)
        {
            return null;
        }

        int m = sensors.Count;
        int n = snapshots.Count;

        for (int s = 0; s < n; s++)
        {
            if (snapshots[s] is null || snapshots[s].Length != m)
            {
                return null;
            }
        }

        // Build data matrix X (m x n) in column-major order.
        var data = new Complex[m, n];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++)
            {
                data[i, j] = snapshots[j][i];
            }
        }

        var X = Matrix<Complex>.Build.DenseOfArray(data);

        // Sample covariance matrix R = X * X^H / n.
        var R = (X * X.ConjugateTranspose()) / n;

        // Hermitian EVD of the covariance matrix.
        var evd = R.Evd(Symmetricity.Hermitian);
        var eigenValues = evd.EigenValues;
        var eigenVectors = evd.EigenVectors;

        // Pair and sort eigenvectors by descending real eigenvalue.
        var indexed = new (int Index, double Real)[m];
        for (int i = 0; i < m; i++)
        {
            indexed[i] = (i, eigenValues[i].Real);
        }

        Array.Sort(indexed, (a, b) => b.Real.CompareTo(a.Real));

        int noiseCount = m - sourceCount;
        var noiseIndices = new int[noiseCount];
        for (int i = 0; i < noiseCount; i++)
        {
            noiseIndices[i] = indexed[sourceCount + i].Index;
        }

        // Build the noise subspace matrix (m x noiseCount).
        var noiseSubspace = Matrix<Complex>.Build.Dense(m, noiseCount, (i, j) => eigenVectors[i, noiseIndices[j]]);

        int gridCount = (int)(180.0 / stepDegrees) + 1;
        var angles = new double[gridCount];
        var spectrum = new double[gridCount];

        var a = MathNet.Numerics.LinearAlgebra.Vector<Complex>.Build.Dense(m);

        for (int k = 0; k < gridCount; k++)
        {
            double theta = -90.0 + k * stepDegrees;
            angles[k] = theta;
            double rad = theta * Math.PI / 180.0;

            // Plane wave direction vector (azimuth measured from +Y).
            double ux = Math.Sin(rad);
            double uy = Math.Cos(rad);

            for (int i = 0; i < m; i++)
            {
                double distance = (sensors[i].X * ux + sensors[i].Y * uy) / wavelength;
                a[i] = Complex.Exp(new Complex(0.0, -2.0 * Math.PI * distance));
            }

            // Project the steering vector onto the noise subspace:
            // P_noise = E_n * E_n^H * a, then ||P_noise||^2.
            var projection = noiseSubspace.ConjugateTranspose() * a;
            double noiseEnergy = 0.0;
            for (int i = 0; i < projection.Count; i++)
            {
                double rm = projection[i].Real;
                double im = projection[i].Imaginary;
                noiseEnergy += rm * rm + im * im;
            }

            // MUSIC pseudo-spectrum (higher is better).
            spectrum[k] = noiseEnergy > 0.0 ? 1.0 / noiseEnergy : 0.0;
        }

        return new AoaResult(angles, spectrum);
    }
}
