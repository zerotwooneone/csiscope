#pragma once
#include <Arduino.h>
#include <Wire.h>
#include "config.h"
#include "protocol_types.h"

class HardwareDiagnostics
{
public:
    static void executePOST();
    static bool scanForBno085();
    static void setLedState(SystemState state);
    static String getMacAddress();

private:
    static void checkMemoryAllocation();
    static void checkClockSpeeds();
};