#include "RfManager.h"

#include "HardwareDiagnostics.h"
#include "protocol_types.h"
#include <algorithm>
#include <ArduinoJson.h>
#include <cstdio>
#include <vector>

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
String RfManager::_passiveMacFilter;
uint16_t RfManager::_dwellMs = 500;
uint8_t RfManager::_channelIndex = 0;
RfManager::ChannelMetrics RfManager::_metrics = {};

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
    _passiveMacFilter = String();
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
        esp_wifi_set_promiscuous(false);
    }
}

void RfManager::setChannel(uint8_t channel)
{
    ensureStarted();
    esp_wifi_set_channel(channel, WIFI_SECOND_CHAN_NONE);
}

void RfManager::startPassive(uint8_t channel, uint8_t bw, const char* macFilter)
{
    _sweepActive = false;
    _singleChannelActive = false;
    _passiveActive = true;
    _passiveChannel = channel;
    _passiveBw = bw;
    _passiveMacFilter = macFilter != nullptr ? String(macFilter) : String();

    ensureStarted();
    setChannel(channel);
    esp_wifi_set_promiscuous(true);
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
    _metrics.macStats.clear();
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

    if (_metrics.macStats.size() >= 32)
    {
        // Evict the least-seen transmitter to cap memory use.
        auto weakest = _metrics.macStats.begin();
        for (auto it = _metrics.macStats.begin(); it != _metrics.macStats.end(); ++it)
        {
            if (it->second.packets < weakest->second.packets)
            {
                weakest = it;
            }
        }
        _metrics.macStats.erase(weakest);
    }

    auto it = _metrics.macStats.find(src);
    if (it == _metrics.macStats.end())
    {
        MacMetrics m = {};
        m.mac = src;
        m.rssiMin = rssi;
        m.rssiMax = rssi;
        m.rssiSum = rssi;
        m.packets = 1;
        m.errors = rxError ? 1 : 0;
        _metrics.macStats[src] = m;
    }
    else
    {
        MacMetrics& m = it->second;
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
    }
}

void RfManager::addTopMacs(JsonDocument& doc)
{
    JsonArray topMacs = doc["top_macs"].to<JsonArray>();

    if (_metrics.macStats.empty())
    {
        return;
    }

    std::vector<MacMetrics> sorted;
    sorted.reserve(_metrics.macStats.size());
    for (const auto& kv : _metrics.macStats)
    {
        sorted.push_back(kv.second);
    }

    std::sort(sorted.begin(), sorted.end(),
              [](const MacMetrics& a, const MacMetrics& b) { return a.packets > b.packets; });

    size_t topCount = std::min(sorted.size(), static_cast<size_t>(3));
    for (size_t i = 0; i < topCount; ++i)
    {
        const MacMetrics& m = sorted[i];
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
