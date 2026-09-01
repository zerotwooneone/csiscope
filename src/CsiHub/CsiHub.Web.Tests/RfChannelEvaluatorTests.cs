using CsiHub.Features.Home.Models;
using CsiHub.Features.Home.Services;
using CsiHub.Ingestion.Models;

namespace CsiHub.Web.Tests;

public class RfChannelEvaluatorTests
{
    [Fact]
    public void Recommends_Most_Congested_Channel_With_Dominant_Stable_Mac()
    {
        var combined = new Dictionary<int, RfChannelAggregate>
        {
            [1] = new()
            {
                Channel = 1,
                Packets = 100,
                Errors = 5,
                RssiAvg = -70,
                DurationMs = 250,
                TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AA:BB:CC:DD:EE:01"] = new()
                    {
                        Mac = "AA:BB:CC:DD:EE:01",
                        Packets = 80,
                        RssiMin = -68,
                        RssiMax = -62,
                        RssiAvg = -65,
                        DurationMs = 250
                    },
                    ["AA:BB:CC:DD:EE:02"] = new()
                    {
                        Mac = "AA:BB:CC:DD:EE:02",
                        Packets = 20,
                        RssiMin = -78,
                        RssiMax = -72,
                        RssiAvg = -75,
                        DurationMs = 250
                    }
                }
            },
            [6] = new()
            {
                Channel = 6,
                Packets = 10,
                Errors = 0,
                RssiAvg = -60,
                DurationMs = 250,
                TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["11:22:33:44:55:66"] = new()
                    {
                        Mac = "11:22:33:44:55:66",
                        Packets = 10,
                        RssiMin = -62,
                        RssiMax = -58,
                        RssiAvg = -60,
                        DurationMs = 250
                    }
                }
            }
        };

        var evaluator = new RfChannelEvaluator();
        var recommendation = evaluator.Recommend(combined);

        Assert.NotNull(recommendation);
        Assert.Equal(1, recommendation.Channel);
        Assert.Equal("AA:BB:CC:DD:EE:01", recommendation.Mac);
        Assert.Contains("highest stable telemetry", recommendation.Reason, StringComparison.OrdinalIgnoreCase);
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
    public void Rank_Orders_By_Highest_Stable_Telemetry()
    {
        var combined = new Dictionary<int, RfChannelAggregate>
        {
            [1] = new()
            {
                Channel = 1,
                Packets = 100,
                Errors = 0,
                RssiAvg = -70,
                DurationMs = 250,
                TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AA:BB:CC:DD:EE:01"] = new()
                    {
                        Mac = "AA:BB:CC:DD:EE:01",
                        Packets = 100,
                        RssiAvg = -65,
                        RssiMin = -70,
                        RssiMax = -60,
                        DurationMs = 250
                    }
                }
            },
            [6] = new()
            {
                Channel = 6,
                Packets = 10,
                Errors = 0,
                RssiAvg = -60,
                DurationMs = 250,
                TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
                {
                    ["11:22:33:44:55:66"] = new()
                    {
                        Mac = "11:22:33:44:55:66",
                        Packets = 10,
                        RssiAvg = -60,
                        RssiMin = -62,
                        RssiMax = -58,
                        DurationMs = 250
                    }
                }
            }
        };

        var rankings = new RfChannelEvaluator().Rank(combined);

        Assert.Equal(2, rankings.Count);
        Assert.Equal(1, rankings[0].Channel);
        Assert.Equal(6, rankings[1].Channel);
    }

    [Fact]
    public void Heavy_Penalty_When_Dominant_Mac_Below_50_Pps()
    {
        var highPps = new RfChannelAggregate
        {
            Channel = 1,
            Packets = 200,
            Errors = 0,
            RssiAvg = -70,
            DurationMs = 1000,
            TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
            {
                ["AA:BB:CC:DD:EE:01"] = new()
                {
                    Mac = "AA:BB:CC:DD:EE:01",
                    Packets = 200,
                    RssiAvg = -70,
                    RssiMin = -72,
                    RssiMax = -68,
                    DurationMs = 1000
                }
            }
        };

        var lowPps = new RfChannelAggregate
        {
            Channel = 6,
            Packets = 40,
            Errors = 0,
            RssiAvg = -50,
            DurationMs = 1000,
            TopMacs = new Dictionary<string, RfMacMetrics>(StringComparer.OrdinalIgnoreCase)
            {
                ["11:22:33:44:55:66"] = new()
                {
                    Mac = "11:22:33:44:55:66",
                    Packets = 40,
                    RssiAvg = -50,
                    RssiMin = -52,
                    RssiMax = -48,
                    DurationMs = 1000
                }
            }
        };

        var combined = new Dictionary<int, RfChannelAggregate>
        {
            [1] = highPps,
            [6] = lowPps
        };

        var recommendation = new RfChannelEvaluator().Recommend(combined);

        Assert.NotNull(recommendation);
        Assert.Equal(1, recommendation.Channel);
    }
}
