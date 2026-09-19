namespace CsiScope.Domain.Model;

/// <summary>
/// A channel worth acquiring, ranked by observed target-MAC activity from
/// the spectrum survey. <see cref="ActivityScore"/> is a normalized
/// packets-per-second figure.
/// </summary>
public readonly record struct ChannelCandidate(
    WifiChannel Channel,
    MacAddress TopMac,
    double ActivityScore);
