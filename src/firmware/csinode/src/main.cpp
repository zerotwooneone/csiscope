#include <Arduino.h>
#include <ArduinoJson.h>
#include "config.h"
#include "protocol_types.h"
#include "HardwareDiagnostics.h"

// Global State
SystemState currentState = SystemState::STATE_BOOT;
NodeRole currentRole = NodeRole::NONE;
String nodeMacAddress;
unsigned long lastHeartbeatMs = 0;

void setup()
{
  // Opens its USB-CDC serial port[cite: 1]
  Serial.begin(Config::SERIAL_BAUD);

  // Wait for USB connection to establish
  unsigned long bootWait = millis();
  while (!Serial && millis() - bootWait < 2000)
  {
    delay(10);
  }

  HardwareDiagnostics::setLedState(currentState);
  nodeMacAddress = HardwareDiagnostics::getMacAddress();

  // Broadcasts a boot status report and MAC address[cite: 1]
  JsonDocument bootDoc;
  bootDoc["type"] = "boot";
  bootDoc["mac"] = nodeMacAddress;
  serializeJson(bootDoc, Serial);
  Serial.println();

  HardwareDiagnostics::executePOST();
  HardwareDiagnostics::scanForBno085();

  // Transition to Standby
  currentState = SystemState::STATE_STANDBY;
  HardwareDiagnostics::setLedState(currentState);
}

void loop()
{
  unsigned long currentMs = millis();

  // Process serial port listener for incoming host commands[cite: 1]
  if (Serial.available())
  {
    // Handle NDJSON parsing and state switching here
  }

  // State execution matrix
  switch (currentState)
  {
  case SystemState::STATE_STANDBY:
    // Broadcasts a 1Hz heartbeat identifying itself[cite: 1]
    if (currentMs - lastHeartbeatMs >= Config::HEARTBEAT_INTERVAL_MS)
    {
      lastHeartbeatMs = currentMs;

      // Formats strict NDJSON heartbeat payload[cite: 1]
      JsonDocument hbDoc;
      hbDoc["type"] = "hb";
      hbDoc["mac"] = nodeMacAddress;
      hbDoc["role"] = "none";
      hbDoc["state"] = "standby";
      hbDoc["uptime"] = millis() / 1000;

      serializeJson(hbDoc, Serial);
      Serial.println();
    }
    break;

  case SystemState::STATE_STREAMING:
    // Handle high-speed CSI extraction and GPIO syncing
    break;

  default:
    break;
  }
}