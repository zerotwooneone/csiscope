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

private:
    static bool _isLeader;
    static volatile bool _pulse;
    static bool _isrAttached;

    static void IRAM_ATTR onSyncIsr();

    static bool startLeader();
    static bool startFollower();
};
