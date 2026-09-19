namespace CsiScope.Domain.Model;

/// <summary>The three orchestration behaviors of a sensing campaign.</summary>
public enum CampaignMode
{
    /// <summary>Hopping channels to map ambient RF activity.</summary>
    Surveying,

    /// <summary>Static dwell building Welford baselines on a locked channel.</summary>
    Acquiring,

    /// <summary>Locked channel, monitoring for amplitude anomalies.</summary>
    Detecting,
}
