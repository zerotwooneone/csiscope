#include "SerialManager.h"
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
}

void SerialManager::process()
{
    // Pull every available byte from the UART hardware in a single non-blocking pass.
    ingestFromSerial();

    // Drain all complete NDJSON lines that have accumulated since the last loop.
    processLines();
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

void SerialManager::processLines()
{
    char line[LINE_BUFFER_SIZE];
    while (tryReadLine(line, sizeof(line)))
    {
        parseAndDispatch(line);
    }
}

bool SerialManager::tryReadLine(char* line, size_t maxLen)
{
    if (head == tail)
    {
        return false;
    }

    size_t scan = tail;
    size_t count = 0;
    bool foundTerminator = false;

    // Scan forward for a newline, wrapping around the ring if necessary.
    while (scan != head)
    {
        char c = rxBuffer[scan];
        if (c == '\n' || c == '\r')
        {
            foundTerminator = true;
            break;
        }
        scan = (scan + 1) % RX_BUFFER_SIZE;
        ++count;
    }

    if (!foundTerminator)
    {
        return false;
    }

    // Copy at most maxLen - 1 characters so the buffer is always null terminated.
    size_t copyLen = (count < maxLen - 1) ? count : maxLen - 1;
    size_t src = tail;
    for (size_t k = 0; k < copyLen; ++k)
    {
        line[k] = rxBuffer[src];
        src = (src + 1) % RX_BUFFER_SIZE;
    }
    line[copyLen] = '\0';

    // Advance the consumer tail past the line and its terminator.
    tail = (scan + 1) % RX_BUFFER_SIZE;
    overflow = false;

    return true;
}

void SerialManager::parseAndDispatch(const char* line)
{
    static JsonDocument doc;
    doc.clear();
    DeserializationError err = deserializeJson(doc, line);
    if (err)
    {
        sendError("parse", err.c_str());
        return;
    }

    const char* cmd = doc["cmd"];
    if (!cmd || *cmd == '\0')
    {
        // Not every NDJSON line from the host is a command; ignore silently.
        return;
    }

    if (strcmp(cmd, "get_config") == 0)
    {
        sendConfig();
    }
    else if (strcmp(cmd, "diag_test") == 0)
    {
        const char* type = doc["type"];
        if (!type)
        {
            sendError("diag_test", "missing_or_invalid_type");
            return;
        }

        if (strcmp(type, "sync") == 0)
        {
            currentState = SystemState::STATE_DIAG_SYNC;
            HardwareDiagnostics::setLedState(currentState);
            sendAck("diag_test", true);
        }
        else if (strcmp(type, "rf") == 0)
        {
            currentState = SystemState::STATE_DIAG_RF;
            HardwareDiagnostics::setLedState(currentState);
            RfManager::startSweep();
            sendAck("diag_test", true);
        }
        else
        {
            sendError("diag_test", "unknown_type");
        }
    }
    else if (strcmp(cmd, "set_rf") == 0)
    {
        int ch = doc["ch"] | 0;
        if (ch < 1 || ch > 13)
        {
            sendError("set_rf", "invalid_channel");
            return;
        }

        currentState = SystemState::STATE_DIAG_RF;
        HardwareDiagnostics::setLedState(currentState);

        int dwellMs = doc["dwell_ms"] | 250;
        if (dwellMs < 50 || dwellMs > 5000)
        {
            sendError("set_rf", "invalid_dwell");
            return;
        }

        RfManager::startSingleChannelScan(static_cast<uint8_t>(ch), static_cast<uint16_t>(dwellMs));
        sendAck("set_rf", true);
    }
    else if (strcmp(cmd, "set_features") == 0)
    {
        bool clockLeader = doc["clock_leader"] | false;
        bool imuHost = doc["imu_host"] | false;

        bool syncOk = SyncManager::apply(clockLeader);
        bool imuOk = ImuManager::apply(imuHost);

        if (!syncOk)
        {
            sendError("set_features", "sync_init_failed", "clock_leader");
        }

        if (!imuOk)
        {
            sendError("set_features", "imu_init_failed", "imu_host");
        }

        if (!syncOk || !imuOk)
        {
            return;
        }

        sendAck("set_features", true);
    }
    else if (strcmp(cmd, "reboot") == 0)
    {
        sendAck("reboot", true);

        // Flush the outgoing NDJSON ack before resetting the SoC.
        Serial.flush();

        ESP.restart();
    }
    else
    {
        sendError(cmd, "unknown_command");
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

    serializeJson(doc, Serial);
    Serial.println();
}

void SerialManager::sendAck(const char* cmd, bool success, const char* reason)
{
    static JsonDocument doc;
    doc.clear();
    doc["type"] = "ack";
    doc["cmd"] = cmd;
    doc["success"] = success;
    if (reason && *reason)
    {
        doc["reason"] = reason;
    }
    doc["state"] = stateToString(currentState);

    serializeJson(doc, Serial);
    Serial.println();
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

    serializeJson(doc, Serial);
    Serial.println();
}
