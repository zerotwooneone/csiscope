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
