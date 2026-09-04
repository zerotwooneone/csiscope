#pragma once

#include <ArduinoJson.h>
#include <cstddef>
#include <cstdint>

namespace SerialFraming
{
    /// <summary>
    /// Magic bytes that begin every frame. Boot logs and other noise cannot
    /// be mistaken for valid frames.
    /// </summary>
    constexpr uint8_t MagicHigh = 0xA5;
    constexpr uint8_t MagicLow = 0x5A;

    /// <summary>
    /// 2 magic bytes + 2 length bytes + 4 CRC-32 bytes.
    /// </summary>
    constexpr size_t FrameOverhead = 2 + sizeof(uint16_t) + sizeof(uint32_t);

    /// <summary>
    /// Frames a raw payload and enqueues it. frameBuffer must be at least
    /// payloadLen + FrameOverhead bytes.
    /// </summary>
    bool sendFramed(const uint8_t* payload, size_t payloadLen, uint8_t* frameBuffer, size_t frameBufferSize);

    /// <summary>
    /// Serializes a JsonDocument, frames it, and enqueues it. For small documents.
    /// </summary>
    bool sendFramedJson(const JsonDocument& doc);

    /// <summary>
    /// Frames a NUL-terminated diagnostic string and enqueues it. For small text.
    /// </summary>
    bool sendFramedText(const char* text);
}
