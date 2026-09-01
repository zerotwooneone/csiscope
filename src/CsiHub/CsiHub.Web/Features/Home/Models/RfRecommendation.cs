using CsiHub.Ingestion.Models;

namespace CsiHub.Features.Home.Models;

/// <summary>
/// A channel and target MAC recommendation produced by <see cref="RfChannelEvaluator"/>.
/// </summary>
public sealed class RfRecommendation
{
    /// <summary>
    /// Recommended 802.11 channel (1-13).
    /// </summary>
    public int Channel { get; set; }

    /// <summary>
    /// Recommended target transmitter MAC to use as a filter.
    /// </summary>
    public string? Mac { get; set; }

    /// <summary>
    /// Quality score where higher is better (0.0 - 1.0+).
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Human-readable justification for the recommendation.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Aggregate data for the recommended channel.
    /// </summary>
    public RfChannelAggregate? Aggregate { get; set; }

    /// <summary>
    /// All top MACs observed on the recommended channel.
    /// </summary>
    public IReadOnlyCollection<RfMacMetrics> TopMacs { get; set; } = Array.Empty<RfMacMetrics>();
}
