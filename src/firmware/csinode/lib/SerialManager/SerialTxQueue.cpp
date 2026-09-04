#include "SerialTxQueue.h"
#include <Arduino.h>
#include <cstring>
#include <freertos/FreeRTOS.h>
#include <freertos/ringbuf.h>

static RingbufHandle_t s_txRing = nullptr;

// One frame can remain in-flight across drain() calls because Serial.write()
// on USB-CDC may return fewer bytes than requested.
static const uint8_t* s_inFlight = nullptr;
static size_t s_inFlightLen = 0;
static size_t s_inFlightOffset = 0;

bool SerialTxQueue::begin()
{
    if (s_txRing != nullptr)
    {
        return true;
    }

    s_txRing = xRingbufferCreate(TxRingSize, RINGBUF_TYPE_NOSPLIT);
    return s_txRing != nullptr;
}

void SerialTxQueue::end()
{
    if (s_txRing != nullptr)
    {
        vRingbufferDelete(s_txRing);
        s_txRing = nullptr;
    }

    s_inFlight = nullptr;
    s_inFlightLen = 0;
    s_inFlightOffset = 0;
}

bool SerialTxQueue::enqueue(const uint8_t* data, size_t len)
{
    if (s_txRing == nullptr || data == nullptr || len == 0)
    {
        return false;
    }

    if (len > TxRingSize)
    {
        return false;
    }

    return xRingbufferSend(s_txRing, data, len, 0) == pdTRUE;
}

bool SerialTxQueue::enqueue(const char* data)
{
    if (data == nullptr)
    {
        return false;
    }

    return enqueue(reinterpret_cast<const uint8_t*>(data), strlen(data));
}

bool SerialTxQueue::enqueueJson(const JsonDocument& doc)
{
    char buffer[256];
    size_t written = serializeJson(doc, buffer, sizeof(buffer));
    if (written >= sizeof(buffer) - 1)
    {
        // JSON did not fit; do not emit a truncated NDJSON line.
        return false;
    }

    buffer[written] = '\n';
    return enqueue(reinterpret_cast<const uint8_t*>(buffer), written + 1);
}

void SerialTxQueue::drain()
{
    if (s_txRing == nullptr)
    {
        return;
    }

    // Bound the work per loop() tick so the rest of the system stays responsive.
    for (int iteration = 0; iteration < 16; ++iteration)
    {
        if (s_inFlight == nullptr)
        {
            size_t len = 0;
            s_inFlight = static_cast<const uint8_t*>(xRingbufferReceive(s_txRing, &len, 0));
            if (s_inFlight == nullptr)
            {
                return;
            }

            s_inFlightLen = len;
            s_inFlightOffset = 0;
        }

        size_t remaining = s_inFlightLen - s_inFlightOffset;
        int available = Serial.availableForWrite();
        if (available <= 0)
        {
            return;
        }

        size_t toWrite = remaining;
        if (toWrite > static_cast<size_t>(available))
        {
            toWrite = static_cast<size_t>(available);
        }

        size_t sent = Serial.write(s_inFlight + s_inFlightOffset, toWrite);
        if (sent == 0)
        {
            // USB TX ring is momentarily full; resume on the next drain() call.
            return;
        }

        s_inFlightOffset += sent;

        if (s_inFlightOffset >= s_inFlightLen)
        {
            vRingbufferReturnItem(s_txRing, const_cast<void*>(static_cast<const void*>(s_inFlight)));
            s_inFlight = nullptr;
            s_inFlightLen = 0;
            s_inFlightOffset = 0;
        }
    }
}

size_t SerialTxQueue::availableForWrite()
{
    if (s_txRing == nullptr)
    {
        return 0;
    }

    return xRingbufferGetCurFreeSize(s_txRing);
}
