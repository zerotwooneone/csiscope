namespace CsiScope.Domain.Model;

/// <summary>
/// Received signal strength in dBm. Physically bounded to (-127, 0] —
/// a positive RSSI is not a real Wi-Fi measurement.
/// </summary>
public readonly record struct Rssi
{
    public Rssi(short value)
    {
        if (value is < -127 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "RSSI must be in (-127, 0] dBm.");
        }

        Value = value;
    }

    public short Value { get; }

    public static implicit operator short(Rssi rssi) => rssi.Value;

    public static explicit operator Rssi(short value) => new(value);

    public override string ToString() => $"{Value} dBm";
}
