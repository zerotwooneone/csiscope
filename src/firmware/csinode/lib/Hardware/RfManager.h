#pragma once

#include <Arduino.h>
#include <cstdint>
#include <esp_wifi.h>

/// <summary>
/// Non-blocking RF survey manager for the ESP32-S3.
/// Drives promiscuous-mode channel sweeps and aggregates per-dwell metrics
/// (RSSI, packet count, error count) into a single NDJSON payload per channel.
/// </summary>
class RfManager
{
public:
    static void begin();

    /// <summary>
    /// Call each loop() pass while in STATE_DIAG_RF.
    /// Advances the dwell timer and emits a summary when each channel completes.
    /// </summary>
    static void update();

    /// <summary>
    /// Starts a one-shot 1-13 channel sweep. Each channel is sampled for dwellMs.
    /// </summary>
    static void startSweep(uint16_t dwellMs = 500);

    /// <summary>
    /// Starts a single-channel RF diagnostic scan for dwellMs, then emits one
    /// rf_scan NDJSON payload and returns to standby.
    /// </summary>
    static void startSingleChannelScan(uint8_t channel, uint16_t dwellMs = 250);

    /// <summary>
    /// Stops an active sweep/scan and disables promiscuous mode.
    /// </summary>
    static void stop();

    /// <summary>
    /// Sets the Wi-Fi radio to a specific channel (manual override).
    /// </summary>
    static void setChannel(uint8_t channel);

    /// <summary>
    /// True while the channel sweep is still in progress.
    /// </summary>
    static bool isSweeping() { return _sweepActive; }

private:
    struct ChannelMetrics
    {
        uint8_t channel;
        int8_t rssiMin;
        int8_t rssiMax;
        int32_t rssiSum;
        uint32_t packets;
        uint32_t errors;
        unsigned long startMs;
    };

    static bool _started;
    static bool _sweepActive;
    static bool _singleChannelActive;
    static uint8_t _singleChannel;
    static uint16_t _dwellMs;
    static uint8_t _channelIndex;
    static ChannelMetrics _metrics;

    static const uint8_t _channels[];
    static const uint8_t _channelCount;

    static void ensureStarted();
    static void resetMetrics(uint8_t channel);
    static void emitMetrics();
    static void promiscuousCallback(void* buf, wifi_promiscuous_pkt_type_t type);
};
