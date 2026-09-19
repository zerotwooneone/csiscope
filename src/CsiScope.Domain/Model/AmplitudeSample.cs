namespace CsiScope.Domain.Model;

/// <summary>
/// One scalar telemetry reading: amplitude attenuation + RSSI for a link at
/// an instant. Immutable value type — passes by <c>in</c> on the hot path
/// with zero heap allocation.
/// </summary>
public readonly record struct AmplitudeSample(
    LinkIdentity Link,
    DateTimeOffset Timestamp,
    double Amplitude,
    short Rssi);
