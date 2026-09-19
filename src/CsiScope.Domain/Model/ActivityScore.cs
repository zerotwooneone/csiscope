namespace CsiScope.Domain.Model;

/// <summary>
/// Normalized channel activity — observed packets-per-second for a channel
/// during a survey dwell. Non-negative; comparable so thresholds and
/// candidate ranking stay strongly typed.
/// </summary>
public readonly record struct ActivityScore : IComparable<ActivityScore>
{
    public ActivityScore(double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Activity score must be a non-negative number.");
        }

        Value = value;
    }

    public double Value { get; }

    public int CompareTo(ActivityScore other) => Value.CompareTo(other.Value);

    public static bool operator <(ActivityScore left, ActivityScore right) => left.Value < right.Value;

    public static bool operator >(ActivityScore left, ActivityScore right) => left.Value > right.Value;

    public static bool operator <=(ActivityScore left, ActivityScore right) => left.Value <= right.Value;

    public static bool operator >=(ActivityScore left, ActivityScore right) => left.Value >= right.Value;

    public static implicit operator double(ActivityScore score) => score.Value;

    public static explicit operator ActivityScore(double value) => new(value);

    public override string ToString() => $"{Value:F1} pps";
}
