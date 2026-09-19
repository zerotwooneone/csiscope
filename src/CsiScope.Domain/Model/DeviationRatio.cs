namespace CsiScope.Domain.Model;

/// <summary>
/// Tripwire magnitude: squared amplitude deviation divided by the locked
/// variance floor. A ratio above the tripwire multiplier means the frame
/// deviated further than baseline noise explains. Non-negative.
/// </summary>
public readonly record struct DeviationRatio
{
    public DeviationRatio(double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Deviation ratio must be a non-negative number.");
        }

        Value = value;
    }

    public double Value { get; }

    public static implicit operator double(DeviationRatio ratio) => ratio.Value;

    public static explicit operator DeviationRatio(double value) => new(value);

    public override string ToString() => $"{Value:F1}x floor";
}
