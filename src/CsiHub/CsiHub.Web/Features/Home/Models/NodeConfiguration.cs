namespace CsiHub.Features.Home.Models;

/// <summary>
/// Persistent hardware feature flags for a single node, keyed by MAC address.
/// </summary>
public sealed class NodeConfiguration
{
    public string Mac { get; set; } = string.Empty;

    public bool ClockLeader { get; set; }

    public bool ImuHost { get; set; }
}
