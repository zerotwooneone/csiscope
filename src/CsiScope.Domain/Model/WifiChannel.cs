namespace CsiScope.Domain.Model;

/// <summary>
/// Validated 2.4 GHz Wi-Fi channel (1-14). Replaces raw ints everywhere a
/// channel appears in the domain.
/// </summary>
public readonly record struct WifiChannel : IComparable<WifiChannel>
{
    public WifiChannel(int value)
    {
        if (value is < 1 or > 14)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Wi-Fi channel must be 1-14.");
        }

        Value = value;
    }

    public int Value { get; }

    /// <summary>Standard non-overlapping 2.4 GHz channels.</summary>
    public bool IsNonOverlapping => Value is 1 or 6 or 11;

    public int CompareTo(WifiChannel other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();

    public static explicit operator int(WifiChannel channel) => channel.Value;

    public static explicit operator WifiChannel(int value) => new(value);
}
