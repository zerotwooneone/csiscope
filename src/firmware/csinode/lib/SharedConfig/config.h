#pragma once
#include <Arduino.h>

namespace Config
{
    // Hardware Pins
    constexpr uint8_t PIN_I2C_SDA = 8;
    constexpr uint8_t PIN_I2C_SCL = 9;
    constexpr uint8_t PIN_WS2812 = 48;  // Default RGB LED on ESP32-S3 DevKit
    constexpr uint8_t PIN_SYNC_OUT = 4; // GPIO4 pulse generation
    constexpr uint8_t PIN_SYNC_IN = 5;  // GPIO5 interrupt listening

    // Serial & Timings
    constexpr uint32_t SERIAL_BAUD = 921600;
    constexpr uint32_t HEARTBEAT_INTERVAL_MS = 1000;

    // I2C Addresses
    constexpr uint8_t BNO085_ADDR_DEFAULT = 0x4A;
}