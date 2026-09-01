using System.Globalization;
using CsiHub.Features.Home.Models;
using CsiHub.Ingestion.Models;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Evaluates aggregated RF scan results and recommends a channel and target MAC
/// for passive sniffing.
/// </summary>
public sealed class RfChannelEvaluator
{
    /// <summary>
    /// Minimum packets per second a single transmitter must sustain to avoid a
    /// heavy score penalty. Below this, the downstream DSP pipeline will starve.
    /// </summary>
    public const double MinimumPps = 50.0;

    /// <summary>
    /// Recommends the best channel and target MAC from an aggregated 1-13 scan.
    /// Prefers the most congested channel carrying a single strong, stable
    /// transmitter that can sustain at least <see cref="MinimumPps"/> packets/s.
    /// </summary>
    public RfRecommendation? Recommend(IReadOnlyDictionary<int, RfChannelAggregate> combined)
    {
        if (combined.Count == 0)
        {
            return null;
        }

        RfChannelAggregate? bestChannel = null;
        double bestChannelScore = double.MinValue;

        foreach (var channel in combined.Values)
        {
            double score = ScoreChannel(channel);
            if (score > bestChannelScore)
            {
                bestChannelScore = score;
                bestChannel = channel;
            }
        }

        if (bestChannel is null)
        {
            return null;
        }

        var bestMac = bestChannel.TopMacs.Values
            .Select(m => new { Metrics = m, Stats = EvaluateMac(m) })
            .OrderByDescending(x => x.Stats.Score)
            .FirstOrDefault();

        var recommendation = new RfRecommendation
        {
            Channel = bestChannel.Channel,
            Mac = bestMac?.Metrics.Mac,
            Score = bestChannelScore,
            Aggregate = bestChannel,
            TopMacs = bestChannel.TopMacs.Values
                .Select(m => new { Metrics = m, Stats = EvaluateMac(m) })
                .OrderByDescending(x => x.Stats.Score)
                .Select(x => x.Metrics)
                .ToList()
        };

        if (bestMac is null)
        {
            recommendation.Reason = $"Channel {recommendation.Channel} has no dominant transmitter for stable telemetry.";
        }
        else
        {
            var stats = bestMac.Stats;
            var gateStatus = stats.Pps >= MinimumPps
                ? "meets"
                : "falls below";

            recommendation.Reason = $"Channel {recommendation.Channel} has the highest stable telemetry. {bestMac.Metrics.Mac} is the dominant transmitter ({stats.Pps:F0} pps, {bestMac.Metrics.RssiAvg:F1} dBm, {stats.Stability:F0}% stability) and {gateStatus} the {MinimumPps:F0} pps threshold.";
        }

        return recommendation;
    }

    /// <summary>
    /// Returns all channels ranked from best to worst, with a 0-1 quality score.
    /// </summary>
    public IReadOnlyList<RfChannelRanking> Rank(IReadOnlyDictionary<int, RfChannelAggregate> combined)
    {
        return combined.Values
            .Select(c => new RfChannelRanking
            {
                Channel = c.Channel,
                Score = ScoreChannel(c),
                Packets = c.Packets,
                Errors = c.Errors,
                RssiAvg = c.RssiAvg
            })
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>
    /// Higher is better. Rewards a single dominant transmitter with high PPS and
    /// a strong, stable RSSI. Channels without a transmitter meeting the
    /// <see cref="MinimumPps"/> threshold are heavily penalized.
    /// </summary>
    private static double ScoreChannel(RfChannelAggregate channel)
    {
        if (channel.TopMacs.Count == 0)
        {
            return 0.0;
        }

        double bestMacScore = 0.0;
        double bestPps = 0.0;

        foreach (var mac in channel.TopMacs.Values)
        {
            var stats = EvaluateMac(mac);
            if (stats.Score > bestMacScore)
            {
                bestMacScore = stats.Score;
                bestPps = stats.Pps;
            }
        }

        // Threshold gating: a single transmitter must sustain at least 50 pps
        // to keep the DSP pipeline fed.
        if (bestPps < MinimumPps)
        {
            bestMacScore *= 0.1;
        }

        // Small congestion bonus: prefer busier channels overall, but only
        // after the dominant-transmitter criteria are met.
        double congestionBonus = Math.Sqrt(channel.Packets) / 100.0;

        return bestMacScore + congestionBonus;
    }

    /// <summary>
    /// Evaluates a single transmitter. Returns PPS, a stability percentage,
    /// and an overall score where higher is better.
    /// </summary>
    private static (double Pps, double Stability, double Score) EvaluateMac(RfMacMetrics mac)
    {
        double seconds = Math.Max(1, mac.DurationMs) / 1000.0;
        double pps = mac.Packets / seconds;

        // Stronger (less negative) RSSI is better. Map -100..0 dBm to 0..1.
        double rssiStrength = (mac.RssiAvg + 100.0) / 100.0;
        if (rssiStrength <= 0.0)
        {
            rssiStrength = 0.01;
        }
        if (rssiStrength > 1.0)
        {
            rssiStrength = 1.0;
        }

        // Stable signal has a small min-to-max spread. 0 dB spread = 100%,
        // 30 dB spread = ~25%.
        double spread = mac.RssiMax - mac.RssiMin;
        if (spread < 0.0)
        {
            spread = 0.0;
        }
        double stability = 100.0 / (1.0 + spread / 10.0);

        double score = pps * rssiStrength * (stability / 100.0);

        return (pps, stability, score);
    }
}

/// <summary>
/// Ranked channel result from <see cref="RfChannelEvaluator.Rank"/>.
/// </summary>
public sealed class RfChannelRanking
{
    public int Channel { get; set; }

    public double Score { get; set; }

    public long Packets { get; set; }

    public long Errors { get; set; }

    public double RssiAvg { get; set; }
}
