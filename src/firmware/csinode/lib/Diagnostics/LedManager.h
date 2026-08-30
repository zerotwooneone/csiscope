#pragma once

#include <Arduino.h>
#include <FastLED.h>
#include "protocol_types.h"

/// <summary>
/// Non-blocking WS2812 RGB LED manager for the ESP32-S3.
/// Uses millis() delta calculations to produce state-driven patterns without delay().
/// </summary>
class LedManager
{
public:
    static void begin();

    /// <summary>
    /// Call once per loop() with the current millis() value.
    /// </summary>
    static void update(unsigned long now);

    /// <summary>
    /// Set the LED pattern based on the node state.
    /// </summary>
    static void setState(SystemState state);

private:
    static constexpr uint8_t NumLeds = 1;
    static constexpr uint8_t DefaultBrightness = 64;

    static CRGB _leds[NumLeds];
    static SystemState _currentState;

    struct Pattern
    {
        CRGB primary;
        CRGB secondary;
        uint16_t onMs;
        uint16_t offMs;
        bool pulse;
        uint16_t pulsePeriodMs;
    };

    static Pattern patternForState(SystemState state);
    static uint8_t computePulseBrightness(unsigned long now, uint16_t periodMs);
    static void show();
};
