#include "SerialManager.h"
#include "SerialFraming.h"
#include "SerialTxQueue.h"
#include "Crc32.h"
#include "config.h"
#include "HardwareDiagnostics.h"
#include "ImuManager.h"
#include "RfManager.h"
#include "SyncManager.h"

#include <cstring>
#include "esp_system.h"

// Global state exported from main.cpp
extern SystemState currentState;
extern String nodeMacAddress;

// Command parsing uses the default ArduinoJson heap allocator.
// The fixed-size bump allocator was removed because ArduinoJson v7's elastic
// JsonDocument re-allocates its internal pool and leaves unreachable holes in a
// simple bump buffer, which caused NoMemory for even tiny commands.

char SerialManager::rxBuffer[RX_BUFFER_SIZE];
size_t SerialManager::head = 0;
size_t SerialManager::tail = 0;
bool SerialManager::overflow = false;

void SerialManager::begin()
{
    // USB-CDC is initialised in setup(); this class only prepares its buffers.
    head = 0;
    tail = 0;
    overflow = false;
    SerialTxQueue::begin();
}

void SerialManager::process()
{
    // Pull every available byte from the UART hardware in a single non-blocking pass.
    ingestFromSerial();

    // Drain all complete length-prefixed frames that have accumulated since the last loop.
    processFrames();

    // Push any enqueued telemetry/commands to the USB-CDC TX ring. This is
    // done outside of the Wi-Fi callback to avoid task watchdog timeouts.
    SerialTxQueue::drain();
}

const char* SerialManager::stateToString(SystemState state)
{
    switch (state)
    {
    case SystemState::STATE_BOOT:
        return "boot";
    case SystemState::STATE_STANDBY:
        return "standby";
    case SystemState::STATE_STREAMING:
        return "streaming";
    case SystemState::STATE_DIAG_SYNC:
        return "diag_sync";
    case SystemState::STATE_DIAG_IMU:
        return "diag_imu";
    case SystemState::STATE_DIAG_RF:
        return "diag_rf";
    default:
        return "unknown";
    }
}

void SerialManager::ingestFromSerial()
{
    while (Serial.available() > 0)
    {
        int c = Serial.read();
        if (c < 0)
        {
            break;
        }

        if (!pushByte(static_cast<char>(c)))
        {
            if (!overflow)
            {
                overflow = true;
                sendError("rx", "ring_buffer_overflow");
            }
        }
    }
}

bool SerialManager::pushByte(char c)
{
    size_t nextHead = (head + 1) % RX_BUFFER_SIZE;

    // If the buffer would become full, drop the oldest byte to keep the stream alive.
    // This preserves the most recent traffic (critical for 50-100 Hz streaming) while
    // discarding stale data.
    if (nextHead == tail)
    {
        tail = (tail + 1) % RX_BUFFER_SIZE;
    }

    rxBuffer[head] = c;
    head = nextHead;
    return true;
}

void SerialManager::processFrames()
{
    char payload[LINE_BUFFER_SIZE];
    size_t len = 0;
    while (tryReadFrame(payload, sizeof(payload), &len))
    {
        parseAndDispatch(payload);
    }
}

bool SerialManager::tryReadFrame(char* payload, size_t maxPayload, size_t* outLen)
{
    if (payload == nullptr || outLen == nullptr)
    {
        return false;
    }

    while (true)
    {
        size_t available = (head + RX_BUFFER_SIZE - tail) % RX_BUFFER_SIZE;

        // Need at least the magic bytes to scan for a frame.
        if (available < 2)
        {
            return false;
        }

        // Search for the magic sequence, discarding leading noise.
        bool foundMagic = false;
        while (available >= 2)
        {
            uint8_t first = static_cast<uint8_t>(rxBuffer[tail]);
            uint8_t second = static_cast<uint8_t>(rxBuffer[(tail + 1) % RX_BUFFER_SIZE]);
            if (first == SerialFraming::MagicHigh && second == SerialFraming::MagicLow)
            {
                foundMagic = true;
                break;
            }

            tail = (tail + 1) % RX_BUFFER_SIZE;
            --available;
        }

        if (!foundMagic || available < 2)
        {
            return false;
        }

        // Need the full 4-byte header.
        if (available < 4)
        {
            return false;
        }

        uint8_t lenLow = static_cast<uint8_t>(rxBuffer[(tail + 2) % RX_BUFFER_SIZE]);
        uint8_t lenHigh = static_cast<uint8_t>(rxBuffer[(tail + 3) % RX_BUFFER_SIZE]);
        uint16_t payloadLen = (static_cast<uint16_t>(lenHigh) << 8) | lenLow;

        if (payloadLen == 0 || payloadLen > maxPayload)
        {
            // Bogus length; drop the magic byte and resync.
            tail = (tail + 1) % RX_BUFFER_SIZE;
            continue;
        }

        size_t frameTotal = 4 + payloadLen + 4;
        if (available < frameTotal)
        {
            // Whole frame not yet received; wait for more bytes.
            return false;
        }

        // Copy the payload and compute the CRC over length + payload.
        for (size_t i = 0; i < payloadLen; ++i)
        {
            payload[i] = rxBuffer[(tail + 4 + i) % RX_BUFFER_SIZE];
        }
        payload[payloadLen] = '\0';

        uint32_t expectedCrc = 0;
        for (int i = 0; i < 4; ++i)
        {
            expectedCrc |= static_cast<uint32_t>(
                static_cast<uint8_t>(rxBuffer[(tail + 4 + payloadLen + i) % RX_BUFFER_SIZE])) << (i * 8);
        }

        uint8_t lenBytes[2] = { lenLow, lenHigh };
        uint32_t actualCrc = Crc32::update(0xFFFFFFFF, lenBytes, 2);
        actualCrc = Crc32::update(actualCrc, reinterpret_cast<const uint8_t*>(payload), payloadLen);
        actualCrc = ~actualCrc;

        if (actualCrc != expectedCrc)
        {
            // Corrupt frame; drop the magic byte and resync.
            tail = (tail + 1) % RX_BUFFER_SIZE;
            continue;
        }

        // Valid frame; consume it and return the payload.
        *outLen = payloadLen;
        tail = (tail + frameTotal) % RX_BUFFER_SIZE;
        overflow = false;
        return true;
    }
}

void SerialManager::parseAndDispatch(const char* line)
{
    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, line);
    if (err)
    {
        sendError("parse", err.c_str());
        return;
    }

    const char* cmd = doc["cmd"];
    if (!cmd || *cmd == '\0')
    {
        // Not every framed JSON from the host is a command; ignore silently.
        return;
    }

    int32_t seq = doc["seq"] | 0;

    if (strcmp(cmd, "get_config") == 0)
    {
        sendConfig();
    }
    else if (strcmp(cmd, "diag_test") == 0)
    {
        const char* type = doc["type"];
        if (!type)
        {
            sendAck("diag_test", false, seq, "missing_or_invalid_type");
            return;
        }

        if (strcmp(type, "sync") == 0)
        {
            currentState = SystemState::STATE_DIAG_SYNC;
            HardwareDiagnostics::setLedState(currentState);
            sendAck("diag_test", true, seq);
        }
        else if (strcmp(type, "rf") == 0)
        {
            currentState = SystemState::STATE_DIAG_RF;
            HardwareDiagnostics::setLedState(currentState);
            RfManager::startSweep();
            sendAck("diag_test", true, seq);
        }
        else
        {
            sendAck("diag_test", false, seq, "unknown_type");
        }
    }
    else if (strcmp(cmd, "set_rf") == 0)
    {
        int ch = doc["ch"] | 0;
        if (ch < 1 || ch > 13)
        {
            sendAck("set_rf", false, seq, "invalid_channel");
            return;
        }

        const char* mode = doc["mode"] | "diag";

        if (strcmp(mode, "passive") == 0)
        {
            int bw = doc["bw"] | 20;

            JsonArrayConst macFilters = doc["mac_filter"].as<JsonArrayConst>();
            if (macFilters.isNull() || macFilters.size() == 0)
            {
                sendAck("set_rf", false, seq, "missing_mac_filter");
                return;
            }
            if (macFilters.size() > RfManager::MaxTargetMacs)
            {
                sendAck("set_rf", false, seq, "too_many_mac_filters");
                return;
            }

            const char* macStrings[RfManager::MaxTargetMacs];
            size_t macCount = 0;
            for (size_t i = 0; i < macFilters.size(); ++i)
            {
                const char* s = macFilters[i].as<const char*>();
                if (s == nullptr || *s == '\0')
                {
                    sendAck("set_rf", false, seq, "invalid_mac_filter");
                    return;
                }
                macStrings[macCount++] = s;
            }

            if (!RfManager::startPassive(static_cast<uint8_t>(ch), static_cast<uint8_t>(bw), macStrings, macCount))
            {
                sendAck("set_rf", false, seq, "invalid_mac_filter");
                return;
            }

            currentState = SystemState::STATE_STREAMING;
            HardwareDiagnostics::setLedState(currentState);
            sendAck("set_rf", true, seq);
            return;
        }

        currentState = SystemState::STATE_DIAG_RF;
        HardwareDiagnostics::setLedState(currentState);

        int dwellMs = doc["dwell_ms"] | 250;
        if (dwellMs < 50 || dwellMs > 5000)
        {
            sendAck("set_rf", false, seq, "invalid_dwell");
            return;
        }

        RfManager::startSingleChannelScan(static_cast<uint8_t>(ch), static_cast<uint16_t>(dwellMs));
        sendAck("set_rf", true, seq);
    }
    else if (strcmp(cmd, "set_features") == 0)
    {
        bool clockLeader = doc["clock_leader"] | false;
        bool imuHost = doc["imu_host"] | false;

        bool syncOk = SyncManager::apply(clockLeader);
        bool imuOk = ImuManager::apply(imuHost);

        if (!syncOk)
        {
            sendAck("set_features", false, seq, "sync_init_failed");
        }

        if (!imuOk)
        {
            sendAck("set_features", false, seq, "imu_init_failed");
        }

        if (!syncOk || !imuOk)
        {
            return;
        }

        sendAck("set_features", true, seq);
    }
    else if (strcmp(cmd, "reboot") == 0)
    {
        sendAck("reboot", true, seq);

        // Send the ack through the staging queue, then reset. The queue does
        // not block, so a stalled host cannot hold the SoC in a flush().
        SerialTxQueue::drain();

        ESP.restart();
    }
    else
    {
        sendAck(cmd, false, seq, "unknown_command");
    }
}

void SerialManager::sendConfig()
{
    static JsonDocument doc;
    doc.clear();
    doc["type"] = "config";
    doc["mac"] = nodeMacAddress;
    doc["state"] = stateToString(currentState);
    doc["baud"] = Config::SERIAL_BAUD;
    doc["bw"] = Config::CSI_BANDWIDTH;
    doc["version"] = "0.1.0";

    SerialFraming::sendFramedJson(doc);
}

void SerialManager::sendAck(const char* cmd, bool success, int32_t seq, const char* reason)
{
    static JsonDocument doc;
    doc.clear();
    doc["type"] = "ack";
    doc["cmd"] = cmd;
    doc["success"] = success;
    doc["seq"] = seq;
    if (reason && *reason)
    {
        doc["reason"] = reason;
    }
    doc["state"] = stateToString(currentState);
    doc["mac"] = nodeMacAddress;

    SerialFraming::sendFramedJson(doc);
}

void SerialManager::sendError(const char* cmd, const char* reason, const char* param)
{
    static JsonDocument doc;
    doc.clear();
    doc["type"] = "error";
    doc["cmd"] = cmd;
    doc["reason"] = reason;

    if (param != nullptr)
    {
        doc["param"] = param;
    }

    SerialFraming::sendFramedJson(doc);
}
