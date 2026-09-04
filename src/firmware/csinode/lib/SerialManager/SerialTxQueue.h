#pragma once

#include <ArduinoJson.h>
#include <cstddef>
#include <cstdint>

namespace SerialTxQueue
{
    /// <summary>
    /// 12 KB staging ring buffer that decouples payload producers from the USB-CDC
    /// <see cref="Serial"/> writer. All outgoing telemetry and commands are enqueued
    /// here, then drained from <see cref="SerialManager::process"/> (loop() context).
    /// </summary>
    constexpr size_t TxRingSize = 12288;

    /// <summary>Initializes the FreeRTOS NOSPLIT ring buffer.</summary>
    bool begin();

    /// <summary>Destroys the ring buffer and releases FreeRTOS memory.</summary>
    void end();

    /// <summary>
    /// Enqueues a raw byte sequence. Returns false if the queue cannot fit the
    /// entire item (the caller should drop the payload and continue).
    /// </summary>
    bool enqueue(const uint8_t* data, size_t len);

    /// <summary>
    /// Enqueues a NUL-terminated string (e.g. a NDJSON diagnostic line).
    /// </summary>
    bool enqueue(const char* data);

    /// <summary>
    /// Serializes a small JsonDocument and enqueues it as a NDJSON line.
    /// </summary>
    bool enqueueJson(const JsonDocument& doc);

    /// <summary>
    /// Drains the queue to <see cref="Serial"/>. Safe to call from loop().
    /// One frame may remain in-flight across calls if <see cref="Serial"/> only
    /// accepted a partial write.
    /// </summary>
    void drain();

    /// <summary>
    /// Returns the largest contiguous free space available for a single item.
    /// </summary>
    size_t availableForWrite();
}
