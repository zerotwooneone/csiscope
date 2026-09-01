#include "RfManager.h"

#include "HardwareDiagnostics.h"
#include "protocol_types.h"
#include <ArduinoJson.h>

// Global state from main.cpp
extern SystemState currentState;
extern String nodeMacAddress;

// Static member definitions
bool RfManager::_started = false;
bool RfManager::_sweepActive = false;
bool RfManager::_singleChannelActive = false;
uint8_t RfManager::_singleChannel = 0;
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
    _singleChannel = 0;
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

    if (pkt->rx_ctrl.rx_state != 0)
    {
        _metrics.errors++;
    }
}
