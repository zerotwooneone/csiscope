namespace CsiScope.Domain.Baselining;

/// <summary>
/// Pure Welford online-mean/variance math, separated from baseline state so
/// the numerics are directly unit-testable. All operations are O(1) and
/// allocation-free.
/// </summary>
public static class BaselineEstimator
{
    /// <summary>
    /// One Welford update step. <paramref name="n"/> is the sample count
    /// AFTER including this sample (1-based).
    /// </summary>
    public static (double Mean, double M2) Update(double mean, double m2, long n, double sample)
    {
        double delta = sample - mean;
        double newMean = mean + (delta / n);
        double newM2 = m2 + (delta * (sample - newMean));
        return (newMean, newM2);
    }

    /// <summary>Unbiased sample variance from the M2 accumulator.</summary>
    public static double Variance(double m2, long n) => n > 1 ? m2 / (n - 1) : 0.0;
}
