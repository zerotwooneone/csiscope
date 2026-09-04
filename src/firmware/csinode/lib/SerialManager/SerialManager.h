#pragma once

#include <Arduino.h>
#include <ArduinoJson.h>
#include "protocol_types.h"

class SerialManager
{
public:
    static constexpr size_t RX_BUFFER_SIZE = 2048;
    static constexpr size_t LINE_BUFFER_SIZE = 512;

    static void begin();
    static void process();
    static void sendConfig();

    static const char* stateToString(SystemState state);

private:
    static char rxBuffer[RX_BUFFER_SIZE];
    static size_t head;
    static size_t tail;
    static bool overflow;

    static void ingestFromSerial();
    static void processFrames();
    static bool tryReadFrame(char* payload, size_t maxPayload, size_t* outLen);
    static bool pushByte(char c);
    static void parseAndDispatch(const char* line);

    static void sendAck(const char* cmd, bool success, int32_t seq = 0, const char* reason = nullptr);
    static void sendError(const char* cmd, const char* reason, const char* param = nullptr);
};
