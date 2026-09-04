using System;
using System.Security.Cryptography;
using System.Text;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Generates a deterministic, highly-visible #RRGGBB color from a MAC address.
/// </summary>
public static class MacColorExtensions
{
    /// <summary>
    /// Maps a MAC string to a stable hex color using SHA256. The RGB channels
    /// are clamped to [0x40, 0xBF] so the color is visible on light and dark themes.
    /// </summary>
    public static string ToMacColor(this string? mac)
    {
        var normalized = (mac ?? string.Empty)
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        int r = ClampChannel(hash[0]);
        int g = ClampChannel(hash[1]);
        int b = ClampChannel(hash[2]);

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static int ClampChannel(int value) => Math.Clamp(value, 0x40, 0xBF);
}
