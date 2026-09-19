namespace CsiScope.Domain.Model;

/// <summary>
/// 48-bit MAC address backed by a single ulong — stack-allocated, no strings
/// on the hot path. Parses both colon-delimited and canonical (unseparated)
/// hex forms.
/// </summary>
public readonly record struct MacAddress : IEquatable<MacAddress>
{
    private const ulong Mask = 0x0000_FFFF_FFFF_FFFFUL;
    private readonly ulong _value;

    private MacAddress(ulong value) => _value = value & Mask;

    public static MacAddress FromUInt64(ulong value) => new(value);

    /// <summary>Parses "AA:BB:CC:DD:EE:FF", "AA-BB-...", or "AABBCCDDEEFF" (case-insensitive).</summary>
    public static MacAddress Parse(ReadOnlySpan<char> text)
    {
        if (!TryParse(text, out var mac))
        {
            throw new FormatException($"Invalid MAC address: '{text.ToString()}'.");
        }

        return mac;
    }

    public static bool TryParse(ReadOnlySpan<char> text, out MacAddress mac)
    {
        mac = default;
        ulong value = 0;
        var nibbles = 0;

        foreach (char c in text)
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }

            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || nibbles >= 12)
            {
                return false;
            }

            value = (value << 4) | (ulong)digit;
            nibbles++;
        }

        if (nibbles != 12)
        {
            return false;
        }

        mac = new MacAddress(value);
        return true;
    }

    /// <summary>IG bit of the first octet — multicast/broadcast frames.</summary>
    public bool IsMulticast => (_value & 0x0100_0000_0000UL) != 0;

    /// <summary>U/L bit of the first octet — locally administered (randomized) MACs.</summary>
    public bool IsLocallyAdministered => (_value & 0x0200_0000_0000UL) != 0;

    public ulong ToUInt64() => _value;

    /// <summary>Canonical colonless uppercase form, e.g. "AABBCCDDEEFF".</summary>
    public string ToCanonicalString() => _value.ToString("X12");

    /// <summary>Colon-delimited form, e.g. "AA:BB:CC:DD:EE:FF".</summary>
    public override string ToString() => string.Create(17, _value, static (span, v) =>
    {
        for (var i = 0; i < 6; i++)
        {
            var b = (byte)(v >> (40 - (i * 8)));
            span[i * 3] = Hex(b >> 4);
            span[(i * 3) + 1] = Hex(b & 0xF);
            if (i < 5)
            {
                span[(i * 3) + 2] = ':';
            }
        }

        static char Hex(int n) => (char)(n < 10 ? '0' + n : 'A' + n - 10);
    });
}
