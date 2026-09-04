#include "SerialFraming.h"
#include "Crc32.h"
#include "SerialTxQueue.h"

#include <Arduino.h>
#include <cstring>

bool SerialFraming::sendFramed(const uint8_t* payload, size_t payloadLen, uint8_t* frameBuffer, size_t frameBufferSize)
{
    if (payload == nullptr || frameBuffer == nullptr)
    {
        return false;
    }

    if (payloadLen > UINT16_MAX || frameBufferSize < payloadLen + FrameOverhead)
    {
        return false;
    }

    frameBuffer[0] = MagicHigh;
    frameBuffer[1] = MagicLow;
    frameBuffer[2] = static_cast<uint8_t>(payloadLen & 0xFF);
    frameBuffer[3] = static_cast<uint8_t>((payloadLen >> 8) & 0xFF);
    std::memcpy(frameBuffer + 4, payload, payloadLen);

    // CRC covers the length field and the payload.
    uint32_t crc = Crc32::update(0xFFFFFFFF, frameBuffer + 2, 2 + payloadLen);
    crc = ~crc;

    size_t trailerOffset = 4 + payloadLen;
    frameBuffer[trailerOffset] = static_cast<uint8_t>(crc & 0xFF);
    frameBuffer[trailerOffset + 1] = static_cast<uint8_t>((crc >> 8) & 0xFF);
    frameBuffer[trailerOffset + 2] = static_cast<uint8_t>((crc >> 16) & 0xFF);
    frameBuffer[trailerOffset + 3] = static_cast<uint8_t>((crc >> 24) & 0xFF);

    return SerialTxQueue::enqueue(frameBuffer, payloadLen + FrameOverhead);
}

bool SerialFraming::sendFramedJson(const JsonDocument& doc)
{
    // Keep one local payload buffer for serialization and a separate frame buffer
    // so there is no aliasing between the two. The payload must be large enough
    // for an rf_scan frame with three top_macs entries, which can exceed 500 bytes.
    char payload[1024];
    size_t written = serializeJson(doc, payload, sizeof(payload));
    if (written >= sizeof(payload))
    {
        return false;
    }

    uint8_t frame[1032];
    return sendFramed(reinterpret_cast<const uint8_t*>(payload), written, frame, sizeof(frame));
}

bool SerialFraming::sendFramedText(const char* text)
{
    if (text == nullptr)
    {
        return false;
    }

    size_t len = std::strlen(text);
    if (len > 256)
    {
        return false;
    }

    uint8_t frame[300];
    return sendFramed(reinterpret_cast<const uint8_t*>(text), len, frame, sizeof(frame));
}
