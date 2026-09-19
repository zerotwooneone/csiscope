namespace CsiScope.Domain.Model;

/// <summary>
/// Aggregate activity floor for a channel: did ANY filtered MAC produce
/// frames in the evaluation window? Distinct from <see cref="ConfidenceScore"/>
/// (primary-target health) so the policy can tell "dead air" apart from
/// "target moved to another channel".
/// </summary>
public readonly record struct ChannelLiveness(
    WifiChannel Channel,
    long FramesDelta,
    DateTimeOffset? LastFrameAt)
{
    /// <summary>True when at least one filtered MAC produced frames.</summary>
    public bool IsAlive => FramesDelta > 0;

    public static ChannelLiveness Dead(WifiChannel channel) => new(channel, 0, null);
}
