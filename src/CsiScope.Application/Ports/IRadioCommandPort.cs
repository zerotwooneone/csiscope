using System.Collections.Immutable;
using CsiScope.Domain.Model;

namespace CsiScope.Application.Ports;

/// <summary>
/// Outbound port for radio commands. The adapter (serial/UDP/ESP-NOW bridge)
/// owns the transport; the orchestrator issues commands fire-and-forget —
/// it never awaits transport completion on the sensing path.
/// </summary>
public interface IRadioCommandPort
{
    /// <summary>
    /// Set the passive listening channel and MAC filter on all nodes.
    /// An empty <paramref name="macFilter"/> means "accept all transmitters"
    /// (used while surveying/auditing). Returns true when the firmware
    /// acknowledged the command; false on timeout/transport failure.
    /// </summary>
    Task<bool> BroadcastSetRfAsync(WifiChannel channel, ImmutableArray<MacAddress> macFilter, CancellationToken ct = default);
}
