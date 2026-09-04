#include "RfManager.h"

#include "HardwareDiagnostics.h"
#include "protocol_types.h"
#include "SyncManager.h"
#include <algorithm>
#include <ArduinoJson.h>
#include <array>
#include <cctype>
#include <cstdio>
#include <cstring>

// Global state from main.cpp
extern SystemState currentState;
extern String nodeMacAddress;

// Static member definitions
bool RfManager::_started = false;
bool RfManager::_sweepActive = false;
bool RfManager::_singleChannelActive = false;
bool RfManager::_passiveActive = false;
uint8_t RfManager::_singleChannel = 0;
uint8_t RfManager::_passiveChannel = 0;
uint8_t RfManager::_passiveBw = 0;
std::array<RfManager::MacAddress, RfManager::MaxTargetMacs> RfManager::_passiveTargetMacs = {};
size_t RfManager::_passiveTargetMacCount = 0;
uint16_t RfManager::_dwellMs = 500;
uint8_t RfManager::_channelIndex = 0;
uint32_t RfManager::_csiSeq = 0;
RfManager::ChannelMetrics RfManager::_metrics = {};

// CSI NDJSON serialization state.
// Pre-warmed in startPassive() so the callback avoids heap allocations on the hot path.
static JsonDocument s_csiDoc;

// Large enough for the full CSI payload at 40 MHz with all LTFs enabled
// plus the NDJSON line terminator (up to ~700-900 I/Q values).
constexpr size_t CsiJsonBufferSize = 8192;
static char s_csiJsonBuffer[CsiJsonBufferSize];

// CSI receive callback registered with esp_wifi_set_csi_rx_cb.
// Defined near the bottom of this file with C linkage.

const uint8_t RfManager::_channels[] = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13};
const uint8_t RfManager::_channelCount = sizeof(RfManager::_channels) / sizeof(RfManager::_channels[0]);

void RfManager::begin()
{
    _started = false;
    _sweepActive = false;
    _singleChannelActive = false;
    _passiveActive = false;
    _singleChannel = 0;
    _passiveChannel = 0;
    _passiveBw = 0;
    _passiveTargetMacs = {};
    _passiveTargetMacCount = 0;
    _csiSeq = 0;
    _channelIndex = 0;
    resetMetrics(_channels[0]);
}

void RfManager::update()
{
    if (!_sweepActive && !_singleChannelActive)
    {
        return;
    }

    unsigned long now = millis();
    if (now - _metrics.startMs < _dwellMs)
    {
        return;
    }

    // Disable promiscuous reception before we read the shared metrics buffer.
    esp_wifi_set_promiscuous(false);

    emitMetrics();

    if (_singleChannelActive)
    {
        _singleChannelActive = false;
        currentState = SystemState::STATE_STANDBY;
        HardwareDiagnostics::setLedState(currentState);
        return;
    }

    _channelIndex++;
    if (_channelIndex >= _channelCount)
    {
        _sweepActive = false;
        currentState = SystemState::STATE_STANDBY;
        HardwareDiagnostics::setLedState(currentState);
        return;
    }

    setChannel(_channels[_channelIndex]);
    resetMetrics(_channels[_channelIndex]);
    esp_wifi_set_promiscuous(true);
}

void RfManager::startSweep(uint16_t dwellMs)
{
    _sweepActive = true;
    _singleChannelActive = false;
    _dwellMs = dwellMs;
    _channelIndex = 0;

    ensureStarted();
    setChannel(_channels[0]);
    resetMetrics(_channels[0]);
    esp_wifi_set_promiscuous(true);
}

void RfManager::startSingleChannelScan(uint8_t channel, uint16_t dwellMs)
{
    _sweepActive = false;
    _singleChannelActive = true;
    _singleChannel = channel;
    _dwellMs = dwellMs;

    ensureStarted();
    setChannel(channel);
    resetMetrics(channel);
    esp_wifi_set_promiscuous(true);
}

void RfManager::stop()
{
    _sweepActive = false;
    _singleChannelActive = false;
    _passiveActive = false;
    if (_started)
    {
        esp_wifi_set_csi(false);
        esp_wifi_set_promiscuous(false);
    }
}

void RfManager::setChannel(uint8_t channel)
{
    ensureStarted();
    esp_wifi_set_channel(channel, WIFI_SECOND_CHAN_NONE);
}

bool RfManager::startPassive(uint8_t channel, uint8_t bw, const char* const* macFilters, size_t count)
{
    _sweepActive = false;
    _singleChannelActive = false;
    _passiveActive = true;
    _passiveChannel = channel;
    _passiveBw = bw;
    _passiveTargetMacs = {};
    _passiveTargetMacCount = 0;
    _csiSeq = 0;

    if (count == 0 || count > MaxTargetMacs)
    {
        return false;
    }

    for (size_t i = 0; i < count; ++i)
    {
        if (macFilters[i] == nullptr || !parseMacString(macFilters[i], _passiveTargetMacs[i]))
        {
            _passiveActive = false;
            _passiveTargetMacCount = 0;
            return false;
        }
    }
    _passiveTargetMacCount = count;

    ensureStarted();

    // Configure CSI to capture all LTF segments and merge them for HT packets.
    wifi_csi_config_t csiConfig = {};
    csiConfig.lltf_en = true;
    csiConfig.htltf_en = true;
    csiConfig.stbc_htltf2_en = true;
    csiConfig.ltf_merge_en = true;
    csiConfig.channel_filter_en = true;
    csiConfig.manu_scale = false;
    csiConfig.shift = 0;
    csiConfig.dump_ack_en = false;
    esp_wifi_set_csi_config(&csiConfig);
    esp_wifi_set_csi(true);

    // Pre-warm the JsonDocument pool so the CSI callback avoids heap allocation.
    prewarmCsiDoc();

    // 40 MHz passive sniffing uses the channel above as the secondary channel.
    wifi_second_chan_t second = (bw >= 40) ? WIFI_SECOND_CHAN_ABOVE : WIFI_SECOND_CHAN_NONE;
    esp_wifi_set_channel(channel, second);
    esp_wifi_set_promiscuous(true);

    return true;
}

void RfManager::ensureStarted()
{
    if (_started)
    {
        return;
    }

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    esp_err_t initResult = esp_wifi_init(&cfg);
    if (initResult != ESP_OK && initResult != ESP_ERR_INVALID_STATE)
    {
        // Initialization failure is logged but not fatal here.
        return;
    }

    esp_wifi_set_mode(WIFI_MODE_STA);
    esp_wifi_start();

    wifi_promiscuous_filter_t filter = {};
    filter.filter_mask = WIFI_PROMIS_FILTER_MASK_ALL;
    esp_wifi_set_promiscuous_filter(&filter);
    esp_wifi_set_promiscuous_rx_cb(promiscuousCallback);

    // Register the CSI callback once. CSI is only enabled while passive sniffing.
    esp_wifi_set_csi_rx_cb(csiRxCallback, nullptr);
    esp_wifi_set_csi(false);

    _started = true;
}

void RfManager::resetMetrics(uint8_t channel)
{
    _metrics.channel = channel;
    _metrics.rssiMin = INT8_MAX;
    _metrics.rssiMax = INT8_MIN;
    _metrics.rssiSum = 0;
    _metrics.packets = 0;
    _metrics.errors = 0;
    _metrics.startMs = millis();
    _metrics.macTable = {};
    _metrics.macTableCount = 0;
}

void RfManager::emitMetrics()
{
    unsigned long elapsed = millis() - _metrics.startMs;
    double rssiAvg = _metrics.packets > 0
        ? static_cast<double>(_metrics.rssiSum) / static_cast<double>(_metrics.packets)
        : 0.0;

    static JsonDocument doc;
    doc.clear();
    doc["type"] = "rf_scan";
    doc["mac"] = nodeMacAddress;
    doc["ch"] = _metrics.channel;
    doc["rssi_min"] = _metrics.packets > 0 ? _metrics.rssiMin : 0;
    doc["rssi_max"] = _metrics.packets > 0 ? _metrics.rssiMax : 0;
    doc["rssi_avg"] = rssiAvg;
    doc["packets"] = _metrics.packets;
    doc["errors"] = _metrics.errors;
    doc["duration_ms"] = elapsed;
    doc["timestamp"] = millis() / 1000;

    addTopMacs(doc);

    serializeJson(doc, Serial);
    Serial.println();
}

void RfManager::promiscuousCallback(void* buf, wifi_promiscuous_pkt_type_t type)
{
    (void)type;

    if ((!_sweepActive && !_singleChannelActive) || buf == nullptr)
    {
        return;
    }

    wifi_promiscuous_pkt_t* pkt = static_cast<wifi_promiscuous_pkt_t*>(buf);
    int8_t rssi = pkt->rx_ctrl.rssi;
    bool rxError = pkt->rx_ctrl.rx_state != 0;

    _metrics.packets++;
    _metrics.rssiSum += rssi;

    if (rssi < _metrics.rssiMin)
    {
        _metrics.rssiMin = rssi;
    }

    if (rssi > _metrics.rssiMax)
    {
        _metrics.rssiMax = rssi;
    }

    if (rxError)
    {
        _metrics.errors++;
    }

    updateMacMetrics(pkt, rssi, rxError);
}

void RfManager::updateMacMetrics(wifi_promiscuous_pkt_t* pkt, int8_t rssi, bool rxError)
{
    uint16_t length = pkt->rx_ctrl.sig_len;

    if (length < 16)
    {
        return;
    }

    const uint8_t* payload = pkt->payload;
    uint8_t frameType = payload[0] & 0x0C;

    // Skip control frames; they do not have a standard Address2 transmitter field.
    if (frameType == 0x04)
    {
        return;
    }

    MacAddress src = { payload[10], payload[11], payload[12], payload[13], payload[14], payload[15] };

    for (size_t i = 0; i < _metrics.macTableCount; ++i)
    {
        if (_metrics.macTable[i].mac == src)
        {
            MacMetrics& m = _metrics.macTable[i];
            m.packets++;
            m.rssiSum += rssi;
            if (rssi < m.rssiMin)
            {
                m.rssiMin = rssi;
            }
            if (rssi > m.rssiMax)
            {
                m.rssiMax = rssi;
            }
            if (rxError)
            {
                m.errors++;
            }
            return;
        }
    }

    if (_metrics.macTableCount < MacTableSize)
    {
        MacMetrics& m = _metrics.macTable[_metrics.macTableCount++];
        m.mac = src;
        m.rssiMin = rssi;
        m.rssiMax = rssi;
        m.rssiSum = rssi;
        m.packets = 1;
        m.errors = rxError ? 1 : 0;
    }
    else
    {
        // Evict the least-seen transmitter to keep the table fixed-size.
        size_t victim = 0;
        for (size_t i = 1; i < MacTableSize; ++i)
        {
            if (_metrics.macTable[i].packets < _metrics.macTable[victim].packets)
            {
                victim = i;
            }
        }

        MacMetrics& m = _metrics.macTable[victim];
        m.mac = src;
        m.rssiMin = rssi;
        m.rssiMax = rssi;
        m.rssiSum = rssi;
        m.packets = 1;
        m.errors = rxError ? 1 : 0;
    }
}

void RfManager::addTopMacs(JsonDocument& doc)
{
    if (_metrics.macTableCount == 0)
    {
        return;
    }

    // Sort indices into the fixed table instead of copying or allocating.
    std::array<size_t, MacTableSize> indices = {};
    for (size_t i = 0; i < _metrics.macTableCount; ++i)
    {
        indices[i] = i;
    }

    const auto& table = _metrics.macTable;
    std::sort(indices.begin(), indices.begin() + _metrics.macTableCount,
              [&table](size_t a, size_t b) {
                  if (table[a].packets != table[b].packets)
                  {
                      return table[a].packets > table[b].packets;
                  }
                  return table[a].rssiSum > table[b].rssiSum;
              });

    JsonArray topMacs = doc["top_macs"].to<JsonArray>();
    size_t topCount = std::min(_metrics.macTableCount, static_cast<size_t>(3));
    for (size_t i = 0; i < topCount; ++i)
    {
        const MacMetrics& m = table[indices[i]];
        double rssiAvg = m.packets > 0
            ? static_cast<double>(m.rssiSum) / static_cast<double>(m.packets)
            : 0.0;

        JsonObject obj = topMacs.add<JsonObject>();
        obj["mac"] = formatMac(m.mac);
        obj["packets"] = m.packets;
        obj["errors"] = m.errors;
        obj["rssi_min"] = m.packets > 0 ? m.rssiMin : 0;
        obj["rssi_max"] = m.packets > 0 ? m.rssiMax : 0;
        obj["rssi_avg"] = rssiAvg;
    }
}

String RfManager::formatMac(const MacAddress& mac)
{
    char buf[18];
    std::snprintf(buf, sizeof(buf), "%02X:%02X:%02X:%02X:%02X:%02X",
                  mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return String(buf);
}

bool RfManager::parseMacString(const char* s, MacAddress& out)
{
    if (s == nullptr || *s == '\0')
    {
        return false;
    }

    unsigned int a = 0, b = 0, c = 0, d = 0, e = 0, f = 0;

    if (std::sscanf(s, "%2x:%2x:%2x:%2x:%2x:%2x", &a, &b, &c, &d, &e, &f) != 6)
    {
        // Also accept the unseparated form AABBCCDDEEFF.
        if (std::sscanf(s, "%2x%2x%2x%2x%2x%2x", &a, &b, &c, &d, &e, &f) != 6)
        {
            return false;
        }
    }

    out[0] = static_cast<uint8_t>(a);
    out[1] = static_cast<uint8_t>(b);
    out[2] = static_cast<uint8_t>(c);
    out[3] = static_cast<uint8_t>(d);
    out[4] = static_cast<uint8_t>(e);
    out[5] = static_cast<uint8_t>(f);

    return true;
}

bool RfManager::matchesAnyTargetMac(const uint8_t* mac)
{
    if (_passiveTargetMacCount == 0 || mac == nullptr)
    {
        return false;
    }
    for (size_t i = 0; i < _passiveTargetMacCount; ++i)
    {
        if (std::memcmp(mac, _passiveTargetMacs[i].data(), 6) == 0)
        {
            return true;
        }
    }
    return false;
}

void RfManager::prewarmCsiDoc()
{
    s_csiDoc.clear();
    JsonArray c = s_csiDoc["c"].to<JsonArray>();

    // Pre-warm for the largest 40 MHz payload with all LTFs enabled
    // (well under 1024 I/Q values after first_word_invalid skip).
    for (size_t i = 0; i < 1024; ++i)
    {
        c.add(0);
    }

    s_csiDoc.clear();
}

extern "C" void csiRxCallback(void* ctx, wifi_csi_info_t* info)
{
    (void)ctx;
    if (info == nullptr)
    {
        return;
    }
    RfManager::handleCsi(info);
}

void RfManager::handleCsi(wifi_csi_info_t* info)
{
    // Drop frames unless the system is actively streaming and passive mode is on.
    if (!_passiveActive || currentState != SystemState::STATE_STREAMING)
    {
        return;
    }

    if (info == nullptr || info->buf == nullptr || info->len == 0)
    {
        return;
    }

    // MAC filter: only frames from one of the configured target addresses are serialized.
    if (_passiveTargetMacCount > 0 && !matchesAnyTargetMac(info->mac))
    {
        return;
    }

    const int8_t* csiBuf = info->buf;
    uint16_t csiLen = info->len;

    // The ESP32-S3 marks the first word of the CSI buffer as invalid for some rates.
    if (info->first_word_invalid)
    {
        if (csiLen <= 4)
        {
            return;
        }
        csiBuf += 4;
        csiLen -= 4;
    }

    emitCsi(info, csiBuf, csiLen);
}

void RfManager::emitCsi(wifi_csi_info_t* info, const int8_t* csiBuf, uint16_t csiLen)
{
    (void)info;

    s_csiDoc.clear();
    s_csiDoc["type"] = "csi";
    s_csiDoc["mac"] = nodeMacAddress;

    // Pack the transmitter MAC into a big-endian 64-bit integer to avoid
    // per-frame string allocation on the host.
    uint64_t srcMac = 0;
    for (int i = 0; i < 6; ++i)
    {
        srcMac = (srcMac << 8) | static_cast<uint8_t>(info->mac[i]);
    }
    s_csiDoc["src"] = srcMac;

    s_csiDoc["seq"] = _csiSeq++;
    s_csiDoc["t"] = SyncManager::syncedMicros();
    s_csiDoc["rssi"] = static_cast<int>(info->rx_ctrl.rssi);

    // info->buf is int8_t* with interleaved signed I/Q samples.
    // Cast each byte to int so ArduinoJson stores a signed number, not a byte/char.
    JsonArray c = s_csiDoc["c"].to<JsonArray>();
    for (uint16_t i = 0; i < csiLen; ++i)
    {
        c.add(static_cast<int>(csiBuf[i]));
    }

    // Build the entire NDJSON line before touching the serial buffer so we can
    // atomically write it and never emit a partial JSON object.
    size_t jsonLen = measureJson(s_csiDoc);
    if (jsonLen + 2 > CsiJsonBufferSize)
    {
        return;
    }

    size_t written = serializeJson(s_csiDoc, s_csiJsonBuffer, CsiJsonBufferSize);
    if (written >= CsiJsonBufferSize - 1)
    {
        return;
    }

    s_csiJsonBuffer[written] = '\n';
    s_csiJsonBuffer[written + 1] = '\0';
    size_t lineLen = written + 1;

    if (s_csiDoc.isNull())
    {
        // The JsonDocument could not allocate the root object; do not emit a
        // literal `null` that the host cannot parse as a NodePayload.
        return;
    }

    if (Serial.availableForWrite() < static_cast<int>(lineLen))
    {
        return;
    }

    size_t txBytes = Serial.write(s_csiJsonBuffer, lineLen);
    if (txBytes != lineLen)
    {
        // Only part of the line fit in the TX ring buffer.  Emitting the
        // remainder from a later frame would splice two lines, so drop it.
        return;
    }
}
