#include "LedManager.h"
#include "config.h"
#include "SyncManager.h"

CRGB LedManager::_leds[NumLeds];
SystemState LedManager::_currentState = SystemState::STATE_BOOT;

void LedManager::begin()
{
    FastLED.addLeds<WS2812, Config::PIN_WS2812, GRB>(_leds, NumLeds);
    FastLED.setBrightness(DefaultBrightness);
    _currentState = SystemState::STATE_BOOT;
    _leds[0] = CRGB::Black;
    show();
}

void LedManager::setState(SystemState state)
{
    _currentState = state;
    update(millis());
}

void LedManager::update(unsigned long now)
{
    auto pattern = patternForState(_currentState);

    // A node that has armed as clock leader tints its pattern cyan so the role
    // is visible at a glance: the blink/pulse shape still encodes the state,
    // the hue encodes the role. Only set after apply() succeeds, so a failed
    // leader init never shows the tint.
    if (SyncManager::isLeader())
    {
        pattern.primary = CRGB::Cyan;
    }

    if (pattern.pulse)
    {
        uint8_t brightness = computePulseBrightness(now, pattern.pulsePeriodMs);
        _leds[0] = pattern.primary;
        _leds[0].nscale8(brightness);
    }
    else
    {
        uint16_t cycle = pattern.onMs + pattern.offMs;
        uint16_t phase = static_cast<uint16_t>(now % cycle);

        if (phase < pattern.onMs)
        {
            _leds[0] = pattern.primary;
        }
        else
        {
            _leds[0] = pattern.secondary;
        }
    }

    show();
}

uint8_t LedManager::computePulseBrightness(unsigned long now, uint16_t periodMs)
{
    if (periodMs == 0)
    {
        return 255;
    }

    uint32_t half = periodMs / 2;
    uint32_t phase = now % periodMs;

    // Triangular ramp up then down over the period.
    uint32_t value = (phase < half)
        ? (phase * 255UL) / half
        : ((periodMs - phase) * 255UL) / half;

    return static_cast<uint8_t>(value);
}

LedManager::Pattern LedManager::patternForState(SystemState state)
{
    switch (state)
    {
    case SystemState::STATE_STREAMING:
        // Solid green while actively streaming.
        return { CRGB::Green, CRGB::Green, 1000, 0, false, 1000 };

    case SystemState::STATE_DIAG_SYNC:
        // Rapid flashing red/white while running a sync diagnostic.
        return { CRGB::Red, CRGB::White, 100, 100, false, 200 };

    case SystemState::STATE_DIAG_RF:
        // Blinking blue in RF diagnostic mode.
        return { CRGB::Blue, CRGB::Black, 250, 250, false, 500 };

    case SystemState::STATE_DIAG_IMU:
        // Blinking magenta in IMU diagnostic mode.
        return { CRGB::Magenta, CRGB::Black, 250, 250, false, 500 };

    case SystemState::STATE_STANDBY:
    case SystemState::STATE_BOOT:
    default:
        // Slow pulsing yellow for standby / boot.
        return { CRGB::Yellow, CRGB::Black, 500, 500, true, 2000 };
    }
}

void LedManager::show()
{
    FastLED.show();
}
