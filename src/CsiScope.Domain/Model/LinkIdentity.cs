namespace CsiScope.Domain.Model;

/// <summary>
/// Composite identity of one sensing link: which array node heard which
/// transmitter on which channel. Struct key — usable directly as a
/// dictionary key with no string interning.
/// </summary>
public readonly record struct LinkIdentity(MacAddress Node, MacAddress Source, WifiChannel Channel);
