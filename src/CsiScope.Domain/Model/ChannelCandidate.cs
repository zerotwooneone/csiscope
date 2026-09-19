namespace CsiScope.Domain.Model;

/// <summary>
/// A channel worth acquiring, ranked by observed target-MAC activity from
/// the spectrum survey.
/// </summary>
public readonly record struct ChannelCandidate(
    WifiChannel Channel,
    MacAddress TopMac,
    ActivityScore ActivityScore);
