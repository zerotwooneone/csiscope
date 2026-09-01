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
    /// Recommends the best channel and target MAC from an aggregated 1-13 scan.
    /// Prefers channels with low total traffic and a dominant, strong transmitter.
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
            .OrderByDescending(m => m.Packets)
            .ThenByDescending(m => m.RssiAvg)
            .FirstOrDefault();

        var recommendation = new RfRecommendation
        {
            Channel = bestChannel.Channel,
            Mac = bestMac?.Mac,
            Score = bestChannelScore,
            Aggregate = bestChannel,
            TopMacs = bestChannel.TopMacs.Values
                .OrderByDescending(m => m.Packets)
                .ThenByDescending(m => m.RssiAvg)
                .ToList()
        };

        if (bestMac is null)
        {
            recommendation.Reason = $"Channel {recommendation.Channel} has the lowest congestion but no dominant transmitter was observed.";
        }
        else
        {
            recommendation.Reason = $"Channel {recommendation.Channel} has the lowest congestion and {bestMac.Mac} is the dominant transmitter ({bestMac.Packets} packets, {bestMac.RssiAvg:F1} dBm average).";
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
    /// Higher is better. Heavily penalizes traffic and errors, slightly rewards
    /// stronger (less negative) average RSSI.
    /// </summary>
    private static double ScoreChannel(RfChannelAggregate channel)
    {
        double congestion = channel.Packets + channel.Errors * 10.0;
        double trafficFactor = 1.0 / (1.0 + congestion / 100.0);

        // RSSI is negative; add 100 to make it a positive 0-100-ish factor.
        double rssiFactor = (channel.RssiAvg + 100.0) / 100.0;
        if (rssiFactor <= 0.0)
        {
            rssiFactor = 0.01;
        }

        return trafficFactor * rssiFactor;
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
