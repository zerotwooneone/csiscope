#pragma once

#include <Arduino.h>

/// <summary>
/// Manages the GPIO sync pins: PWM output for the leader and edge-triggered ISR for followers.
/// All setup and teardown is non-blocking; the PWM runs in hardware and the ISR is IRAM_ATTR.
/// </summary>
class SyncManager
{
public:
    static void begin();

    /// <summary>
    /// Apply clock leader/follower configuration. Pass true to generate the sync pulse,
    /// false to listen for it. Returns true if the requested mode was configured.
    /// </summary>
    static bool apply(bool isLeader);

    /// <summary>
    /// True if a sync pulse has been captured since the last call.
    /// </summary>
    static bool hasPulse();

    /// <summary>
    /// Detaches the ISR and stops the PWM. Safe to call multiple times.
    /// </summary>
    static void teardown();

    /// <summary>
    /// Returns the current leader state.
    /// </summary>
    static bool isLeader() { return _isLeader; }

    /// <summary>
    /// Returns a microsecond timestamp anchored to the first sync pulse.
    /// Safe to call from any context; updates are protected by noInterrupts().
    /// </summary>
    static uint32_t syncedMicros();

    /// <summary>
    /// Returns the micros() value captured at the most recent sync pulse.
    /// </summary>
    static uint32_t lastSyncMicros();

    /// <summary>
    /// Resets the diagnostic counters used during STATE_DIAG_SYNC.
    /// </summary>
    static void resetDiagnostics();

    /// <summary>
    /// Returns a snapshot of the sync diagnostic counters and resets them.
    /// </summary>
    static bool getDiagnosticSnapshot(uint32_t& pulseCount, double& latencyUs, double& jitterUs);

private:
    static bool _isLeader;
    static volatile bool _pulse;
    static bool _isrAttached;
    static bool _outputIsrAttached;
    static volatile uint32_t _lastSyncMicros;
    static volatile uint32_t _syncedMicros;

    // Diagnostic accumulators for STATE_DIAG_SYNC telemetry.
    static volatile uint64_t _diagPulseCount;
    static volatile uint64_t _diagLatencySum;
    static volatile uint64_t _diagLatencySqSum;

    // Follower input-edge ISR and leader output-edge ISR both feed syncTick().
    static void IRAM_ATTR onSyncIsr();
    static void IRAM_ATTR onSyncOutputIsr();
    static void IRAM_ATTR syncTick(uint32_t isrStart);

    static bool startLeader();
    static bool startFollower();
};
