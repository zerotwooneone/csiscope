using CsiScope.Domain.Baselining;
using CsiScope.Domain.Model;

namespace CsiScope.Domain.Strategy;

/// <summary>
/// Pure domain service deriving the bifurcated health signals —
/// <see cref="ConfidenceScore"/> (primary target only) and
/// <see cref="ChannelLiveness"/> (any filtered MAC) — from baseline state
/// and frame deltas. No I/O, no clocks: everything arrives as parameters.
/// </summary>
public static class ConfidenceEvaluator
{
    /// <summary>
    /// Confidence for the primary target MAC's links on the locked channel.
    /// Fill = weakest link's window fill (all nodes must learn); Stability =
    /// fraction of target links currently converged; TargetPps = observed
    /// rate vs expected; Age = recency of the newest frame.
    /// </summary>
    public static ConfidenceScore EvaluateConfidence(
        IReadOnlyList<LinkBaseline> targetBaselines,
        double observedPps,
        double expectedPps,
        TimeSpan staleAfter,
        DateTimeOffset now)
    {
        if (targetBaselines.Count == 0)
        {
            return ConfidenceScore.Zero;
        }

        double fill = 1.0;
        var converged = 0;
        var newest = DateTimeOffset.MinValue;
        foreach (var baseline in targetBaselines)
        {
            fill = Math.Min(fill, baseline.FillFraction);
            if (baseline.IsConverged)
            {
                converged++;
            }

            if (baseline.LastFrameAt > newest)
            {
                newest = baseline.LastFrameAt;
            }
        }

        double stability = (double)converged / targetBaselines.Count;
        double pps = expectedPps > 0 ? Math.Min(1.0, observedPps / expectedPps) : 0.0;
        double age = newest == DateTimeOffset.MinValue
            ? 0.0
            : 1.0 - Math.Clamp((now - newest).TotalSeconds / staleAfter.TotalSeconds, 0.0, 1.0);

        return new ConfidenceScore(fill, stability, pps, age);
    }

    /// <summary>
    /// Aggregate channel floor: did ANY filtered MAC produce frames in the
    /// evaluation window? Distinguishes "dead air" from "target moved".
    /// </summary>
    public static ChannelLiveness EvaluateLiveness(
        WifiChannel channel,
        long framesDelta,
        DateTimeOffset? lastFrameAt) =>
        new(channel, framesDelta, lastFrameAt);
}
