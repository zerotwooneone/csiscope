#include "SyncManager.h"
#include "config.h"

bool SyncManager::_isLeader = false;
volatile bool SyncManager::_pulse = false;
bool SyncManager::_isrAttached = false;

static constexpr uint32_t SyncFrequencyHz = 1000;
static constexpr uint8_t SyncPwmResolution = 8;
static constexpr uint8_t SyncPwmDuty = 127; // ~50% duty

// Legacy LEDC API (Arduino-ESP32 v2.x) requires an explicit channel number.
static constexpr uint8_t SyncPwmChannel = 0;

void SyncManager::begin()
{
    _isLeader = false;
    _pulse = false;
    _isrAttached = false;

    // Default to safe input state; apply() will reconfigure when features are set.
    pinMode(Config::PIN_SYNC_OUT, INPUT);
    pinMode(Config::PIN_SYNC_IN, INPUT);
}

bool SyncManager::apply(bool isLeader)
{
    if (_isLeader == isLeader && (isLeader || _isrAttached))
    {
        return true;
    }

    teardown();

    bool ok;
    if (isLeader)
    {
        ok = startLeader();
    }
    else
    {
        ok = startFollower();
    }

    if (ok)
    {
        _isLeader = isLeader;
    }

    return ok;
}

void SyncManager::teardown()
{
    if (_isrAttached)
    {
        detachInterrupt(digitalPinToInterrupt(Config::PIN_SYNC_IN));
        _isrAttached = false;
    }

    if (_isLeader)
    {
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
        ledcDetach(Config::PIN_SYNC_OUT);
#else
        ledcDetachPin(Config::PIN_SYNC_OUT);
        ledcWrite(SyncPwmChannel, 0);
#endif
    }

    _isLeader = false;
}

bool SyncManager::startLeader()
{
    pinMode(Config::PIN_SYNC_OUT, OUTPUT);

    // Hardware square wave on the sync output pin.
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    if (!ledcAttach(Config::PIN_SYNC_OUT, SyncFrequencyHz, SyncPwmResolution))
    {
        return false;
    }
    ledcWrite(Config::PIN_SYNC_OUT, SyncPwmDuty);
    return true;
#else
    ledcSetup(SyncPwmChannel, SyncFrequencyHz, SyncPwmResolution);
    ledcAttachPin(Config::PIN_SYNC_OUT, SyncPwmChannel);
    ledcWrite(SyncPwmChannel, SyncPwmDuty);
    return true;
#endif
}

bool SyncManager::startFollower()
{
    pinMode(Config::PIN_SYNC_IN, INPUT);

    // Edge-triggered interrupt on the sync input pin.
    attachInterrupt(digitalPinToInterrupt(Config::PIN_SYNC_IN), onSyncIsr, RISING);
    _isrAttached = true;
    return true;
}

bool SyncManager::hasPulse()
{
    noInterrupts();
    bool result = _pulse;
    _pulse = false;
    interrupts();
    return result;
}

void IRAM_ATTR SyncManager::onSyncIsr()
{
    _pulse = true;
}
