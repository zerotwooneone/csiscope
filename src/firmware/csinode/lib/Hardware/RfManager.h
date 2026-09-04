#pragma once

#include <Arduino.h>
#include <ArduinoJson.h>
#include <array>
#include <cstdint>
#include <esp_wifi.h>

// Forward declaration of the CSI receive callback, which must use C linkage.
extern "C" void csiRxCallback(void* ctx, wifi_csi_info_t* info);

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
    /// Maximum number of target MAC addresses that can be tracked simultaneously.
    /// </summary>
    static constexpr size_t MaxTargetMacs = 5;

    /// <summary>
    /// Starts passive sniffing on a channel with a target MAC filter.
    /// macFilters is an array of up to MaxTargetMacs NUL-terminated MAC strings.
    /// </summary>
    static bool startPassive(uint8_t channel, uint8_t bw, const char* const* macFilters, size_t count);

    /// <summary>
    /// True while the channel sweep is still in progress.
    /// </summary>
    static bool isSweeping() { return _sweepActive; }

private:
    using MacAddress = std::array<uint8_t, 6>;

    static constexpr size_t MacTableSize = 32;

    struct MacMetrics
    {
        MacAddress mac;
        uint32_t packets;
        uint32_t errors;
        int8_t rssiMin;
        int8_t rssiMax;
        int32_t rssiSum;
    };

    struct ChannelMetrics
    {
        uint8_t channel;
        int8_t rssiMin;
        int8_t rssiMax;
        int32_t rssiSum;
        uint32_t packets;
        uint32_t errors;
        unsigned long startMs;
        std::array<MacMetrics, MacTableSize> macTable = {};
        size_t macTableCount = 0;
    };

    static bool _started;
    static bool _sweepActive;
    static bool _singleChannelActive;
    static bool _passiveActive;
    static uint8_t _singleChannel;
    static uint8_t _passiveChannel;
    static uint8_t _passiveBw;
    static std::array<MacAddress, MaxTargetMacs> _passiveTargetMacs;
    static size_t _passiveTargetMacCount;
    static uint16_t _dwellMs;
    static uint8_t _channelIndex;
    static uint32_t _csiSeq;
    static ChannelMetrics _metrics;

    static const uint8_t _channels[];
    static const uint8_t _channelCount;

    static void ensureStarted();
    static void resetMetrics(uint8_t channel);
    static void emitMetrics();
    static void promiscuousCallback(void* buf, wifi_promiscuous_pkt_type_t type);
    static void updateMacMetrics(wifi_promiscuous_pkt_t* pkt, int8_t rssi, bool rxError);
    static void addTopMacs(JsonDocument& doc);
    static String formatMac(const MacAddress& mac);

    static bool parseMacString(const char* s, MacAddress& out);
    static bool matchesAnyTargetMac(const uint8_t* mac);
    static void handleCsi(wifi_csi_info_t* info);
    static void emitCsi(wifi_csi_info_t* info, const int8_t* csiBuf, uint16_t csiLen);
    static void prewarmCsiDoc();

    friend void csiRxCallback(void* ctx, wifi_csi_info_t* info);
};
