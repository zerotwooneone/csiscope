namespace CsiHub.Core.Tests;

/// <summary>
/// Deterministic synthetic CSI frame generator for baseline math tests.
/// </summary>
public static class TestDataGenerator
{
    /// <summary>
    /// Fills the supplied pre-allocated span with a constant plus Gaussian noise.
    /// Uses the Box-Muller transform and advances the supplied <see cref="Random"/>.
    /// </summary>
    public static void FillConstantWithNoise(
        Span<double> buffer,
        double constant,
        double noiseStdDev,
        Random rng)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = constant + BoxMuller(rng, 0.0, noiseStdDev);
        }
    }

    /// <summary>
    /// Box-Muller transform for a single standard (or scaled/shifted) normal sample.
    /// </summary>
    public static double BoxMuller(Random rng, double mean, double stdDev)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + (z * stdDev);
    }
}

public class RoomBaselineTests
{
    [Theory]
    [InlineData(20, 56)]
    [InlineData(40, 114)]
    public void GetSubcarrierCount_Maps_Bandwidth(int bandwidth, int expectedSubcarriers)
    {
        Assert.Equal(expectedSubcarriers, RoomBaseline.GetSubcarrierCount(bandwidth));
    }

    [Fact]
    public void Initialize_PreAllocates_Per_Iq_Slots()
    {
        var baseline = new RoomBaseline();
        baseline.Initialize(20, 32);

        Assert.Equal(20, baseline.Bandwidth);
        Assert.Equal(56, baseline.SubcarrierCount);
        Assert.Equal(112, baseline.Mean.Length);
        Assert.Equal(112, baseline.Ema.Length);
        Assert.Equal(112, baseline.Variance.Length);
        Assert.Equal(32, baseline.WindowSize);
    }

    [Fact]
    public void Update_Requires_Initialize_Before_First_Call()
    {
        var baseline = new RoomBaseline();
        Assert.Throws<InvalidOperationException>(() => baseline.Update(new double[] { 1.0, 2.0 }));
    }

    [Fact]
    public void Update_Runs_Welford_And_Ema_For_IQ_Stream()
    {
        var baseline = new RoomBaseline();
        baseline.Initialize(20, 64);

        var csi = new double[112];
        for (int i = 0; i < csi.Length; i++)
        {
            csi[i] = i % 2 == 0 ? 10.0 : -10.0;
        }

        for (int sample = 0; sample < 200; sample++)
        {
            baseline.Update(csi);
        }

        var mean = baseline.Mean;
        var variance = baseline.Variance;
        var ema = baseline.Ema;

        for (int i = 0; i < csi.Length; i++)
        {
            var expected = i % 2 == 0 ? 10.0 : -10.0;
            Assert.Equal(expected, mean[i], precision: 10);
            Assert.Equal(0.0, variance[i], precision: 10);
            Assert.Equal(expected, ema[i], precision: 10);
        }
    }

    [Fact]
    public void Initialize_Changes_Bandwidth_And_Resizes_Buffers()
    {
        var baseline = new RoomBaseline();
        baseline.Initialize(20, 8);

        Assert.Equal(112, baseline.Mean.Length);

        baseline.Initialize(40, 8);

        Assert.Equal(40, baseline.Bandwidth);
        Assert.Equal(114, baseline.SubcarrierCount);
        Assert.Equal(228, baseline.Mean.Length);
    }

    [Theory]
    [InlineData(8, 5)]   // filling phase
    [InlineData(8, 8)]   // exactly full
    [InlineData(8, 50)]  // overflow / circular wrap
    public void Windowed_Variance_Matches_Brute_Force(int windowSize, int updates)
    {
        const int slotCount = 4;
        const double constant = 10.0;
        const double noiseStdDev = 1.0;

        var baseline = new RoomBaseline(emaAlpha: 0.2);
        baseline.InitializeFromLength(slotCount, windowSize);

        var rng = new Random(12345);
        var csi = new double[slotCount];
        var history = new double[updates * slotCount];

        for (int u = 0; u < updates; u++)
        {
            TestDataGenerator.FillConstantWithNoise(csi, constant, noiseStdDev, rng);
            csi.AsSpan().CopyTo(history.AsSpan(u * slotCount, slotCount));
            baseline.Update(csi);
        }

        var activeCount = Math.Min(updates, windowSize);
        var start = Math.Max(0, updates - activeCount);

        var expectedMean = new double[slotCount];
        var expectedVariance = new double[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            double sum = 0.0;
            for (int f = start; f < updates; f++)
            {
                sum += history[f * slotCount + i];
            }

            double mean = sum / activeCount;
            expectedMean[i] = mean;

            double m2 = 0.0;
            for (int f = start; f < updates; f++)
            {
                double d = history[f * slotCount + i] - mean;
                m2 += d * d;
            }

            expectedVariance[i] = m2 / activeCount;
        }

        AssertEqual(expectedMean, baseline.Mean);
        AssertEqual(expectedVariance, baseline.Variance, tolerance: 1e-9);
    }

    [Fact]
    public void Update_Does_Not_Allocate()
    {
        const int slotCount = 4;
        const int windowSize = 8;

        var baseline = new RoomBaseline();
        baseline.InitializeFromLength(slotCount, windowSize);

        var rng = new Random(42);
        var csi = new double[slotCount];

        TestDataGenerator.FillConstantWithNoise(csi, 1.0, 0.0, rng);
        baseline.Update(csi); // warm-up: JIT the path and run once

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10_000; i++)
        {
            TestDataGenerator.FillConstantWithNoise(csi, 1.0, 0.0, rng);
            baseline.Update(csi);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    private static void AssertEqual(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual,
        double tolerance = 1e-9)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Math.Abs(expected[i] - actual[i]) <= tolerance,
                $"Index {i}: expected {expected[i]} within {tolerance}, got {actual[i]}");
        }
    }
}
