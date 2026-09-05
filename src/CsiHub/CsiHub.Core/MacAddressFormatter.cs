using System;

namespace CsiHub.Core;

/// <summary>
/// Provides canonical MAC address formatting so live payloads and configured
/// geometry keys can be matched regardless of case, colons, hyphens, or whitespace.
/// </summary>
public static class MacAddressFormatter
{
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
