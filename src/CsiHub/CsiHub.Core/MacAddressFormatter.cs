using System;
using System.Collections.Concurrent;

namespace CsiHub.Core;

/// <summary>
/// Provides canonical MAC address formatting so live payloads and configured
/// geometry keys can be matched regardless of case, colons, hyphens, or whitespace.
/// </summary>
public static class MacAddressFormatter
{
    // The set of distinct MACs seen at runtime is tiny (a handful of nodes plus
    // the configured targets), so an unbounded cache is safe and eliminates the
    // per-frame StringBuilder + string allocation on the DSP hot path.
    private static readonly ConcurrentDictionary<string, string> s_canonicalCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Cached variant of <see cref="ToCanonical"/> for high-frequency callers.
    /// The factory may run more than once under contention, but ToCanonical is
    /// pure so the result is identical; last write wins.
    /// </summary>
    public static string ToCanonicalCached(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return string.Empty;
        }

        return s_canonicalCache.GetOrAdd(mac, static m => ToCanonical(m));
    }

    /// <summary>
    /// True when the locally-administered bit (0x02 of the first octet) is set,
    /// which is how phones randomize probe-request source addresses. Operates on
    /// the canonical string: the second character is the low nibble of octet 0.
    /// </summary>
    public static bool IsLocallyAdministered(string? canonicalMac)
        => canonicalMac is { Length: >= 2 } && "2367ABEF".Contains(canonicalMac[1]);

    /// <summary>
    /// True when the MAC is multicast (0x01) or locally administered (0x02) -
    /// i.e. never a real station's globally unique hardware address.
    /// </summary>
    public static bool IsMulticastOrLocal(string? canonicalMac)
        => canonicalMac is { Length: >= 2 } && (HexValue(canonicalMac[1]) & 0x3) != 0;

    private static int HexValue(char c)
        => c is >= '0' and <= '9' ? c - '0'
         : c is >= 'A' and <= 'F' ? c - 'A' + 10
         : c is >= 'a' and <= 'f' ? c - 'a' + 10
         : 0;

    /// <summary>
    /// Returns an uppercase, colon-free, hyphen-free, whitespace-free MAC string.
    /// Returns <see cref="string.Empty"/> for null or empty input.
    /// </summary>
    public static string ToCanonical(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = mac.AsSpan().Trim();
        var builder = new System.Text.StringBuilder(span.Length);

        foreach (char c in span)
        {
            if (c == ':' || c == '-' || char.IsWhiteSpace(c))
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}
