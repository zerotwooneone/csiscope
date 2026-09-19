using System.Collections.Immutable;

namespace CsiScope.Domain.Model;

/// <summary>
/// Ordered set of channels to dwell on, plus the dwell per channel. Used for
/// survey sweeps and Detecting-state environment audits. Channels are an
/// <see cref="ImmutableArray{T}"/> with sequence equality — two plans with
/// the same contents compare equal regardless of array instance.
/// </summary>
public sealed record ScanPlan(ImmutableArray<WifiChannel> Channels, TimeSpan DwellPerChannel)
{
    public static ScanPlan Empty { get; } = new(ImmutableArray<WifiChannel>.Empty, TimeSpan.Zero);

    public bool Equals(ScanPlan? other) =>
        other is not null
        && DwellPerChannel == other.DwellPerChannel
        && Channels.SequenceEqual(other.Channels);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DwellPerChannel);
        foreach (var channel in Channels)
        {
            hash.Add(channel);
        }

        return hash.ToHashCode();
    }
}
