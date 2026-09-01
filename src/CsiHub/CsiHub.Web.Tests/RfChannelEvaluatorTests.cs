using CsiHub.Features.Home.Models;
using CsiHub.Features.Home.Services;
using CsiHub.Ingestion.Models;

namespace CsiHub.Web.Tests;

public class RfChannelEvaluatorTests
{
    [Fact]
    public void Recommends_Least_Congested_Channel_And_Dominant_Mac()
    {
        var combined = new Dictionary<int, RfChannelAggregate>
        {
            [1] = new()
            {
                Channel = 1,
                Packets = 100,
                Errors = 5,
                RssiAvg = -70,
                TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AA:BB:CC:DD:EE:01"] = new() { Mac = "AA:BB:CC:DD:EE:01", Packets = 80, RssiAvg = -65 },
                    ["AA:BB:CC:DD:EE:02"] = new() { Mac = "AA:BB:CC:DD:EE:02", Packets = 20, RssiAvg = -75 }
                }
            },
            [6] = new()
            {
                Channel = 6,
                Packets = 10,
                Errors = 0,
                RssiAvg = -60,
                TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["11:22:33:44:55:66"] = new() { Mac = "11:22:33:44:55:66", Packets = 10, RssiAvg = -60 }
                }
            }
        };

        var evaluator = new RfChannelEvaluator();
        var recommendation = evaluator.Recommend(combined);

        Assert.NotNull(recommendation);
        Assert.Equal(6, recommendation.Channel);
        Assert.Equal("11:22:33:44:55:66", recommendation.Mac);
        Assert.NotEmpty(recommendation.Reason);
    }

    [Fact]
    public void Recommends_Null_When_No_Data()
    {
        var evaluator = new RfChannelEvaluator();
        var recommendation = evaluator.Recommend(new Dictionary<int, RfChannelAggregate>());

        Assert.Null(recommendation);
    }

    [Fact]
    public void Rank_Orders_By_Quality_Score()
    {
        var combined = new Dictionary<int, RfChannelAggregate>
        {
            [1] = new() { Channel = 1, Packets = 100, Errors = 0, RssiAvg = -70 },
            [6] = new() { Channel = 6, Packets = 10, Errors = 0, RssiAvg = -60 }
        };

        var rankings = new RfChannelEvaluator().Rank(combined);

        Assert.Equal(2, rankings.Count);
        Assert.Equal(6, rankings[0].Channel);
        Assert.Equal(1, rankings[1].Channel);
    }
}
