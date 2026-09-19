namespace CsiScope.Domain.Model;

/// <summary>
/// The locked noise-floor variance for a converged baseline, in squared
/// amplitude units. Tripwires compare live squared deviation against
/// multiplier × floor.
/// </summary>
public readonly record struct VarianceFloor(double Value)
{
    public static VarianceFloor Zero => new(0.0);
}
