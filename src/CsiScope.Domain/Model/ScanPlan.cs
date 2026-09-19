namespace CsiScope.Domain.Model;

/// <summary>
/// Ordered set of channels to dwell on, plus the dwell per channel. Used for
/// survey sweeps and Detecting-state environment audits.
/// </summary>
public sealed record ScanPlan(IReadOnlyList<WifiChannel> Channels, TimeSpan DwellPerChannel)
{
    public static ScanPlan Empty { get; } = new(Array.Empty<WifiChannel>(), TimeSpan.Zero);
}
