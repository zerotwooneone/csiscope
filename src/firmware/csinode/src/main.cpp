#include <Arduino.h>
#include <ArduinoJson.h>
#include "config.h"
#include "protocol_types.h"
#include "HardwareDiagnostics.h"
#include "ImuManager.h"
#include "LedManager.h"
#include "RfManager.h"
#include "SerialManager.h"
#include "SyncManager.h"

// Global State
SystemState currentState = SystemState::STATE_BOOT;
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

  SerialManager::begin();
  LedManager::begin();
  SyncManager::begin();
  ImuManager::begin();
  RfManager::begin();

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

  // Non-blocking NDJSON command ingestion; never waits inside this call.
  SerialManager::process();

  // Service the IMU SHTP bus and cache the latest quaternion.
  ImuManager::update();

  // Update the RGB LED pattern without blocking.
  LedManager::update(currentMs);

  // State execution matrix
  switch (currentState)
  {
  case SystemState::STATE_STANDBY:
    // Broadcasts a 1Hz heartbeat identifying itself[cite: 1]
    if (currentMs - lastHeartbeatMs >= Config::HEARTBEAT_INTERVAL_MS)
    {
      lastHeartbeatMs = currentMs;

      // Formats strict NDJSON heartbeat payload[cite: 1]
      static JsonDocument hbDoc;
      hbDoc.clear();
      hbDoc["type"] = "hb";
      hbDoc["mac"] = nodeMacAddress;
      hbDoc["state"] = SerialManager::stateToString(currentState);
      hbDoc["uptime"] = millis() / 1000;
      hbDoc["bw"] = Config::CSI_BANDWIDTH;
      hbDoc["clock_leader"] = SyncManager::isLeader();
      hbDoc["imu_host"] = ImuManager::isHost();

      serializeJson(hbDoc, Serial);
      Serial.println();
    }
    break;

  case SystemState::STATE_STREAMING:
    // High-speed CSI extraction and GPIO sync is handled by the SyncManager hardware.
    // If the IMU is enabled, append the latest rotation vector to the telemetry stream.
    {
      float qw, qx, qy, qz;
      if (ImuManager::tryGetQuaternion(qw, qx, qy, qz))
      {
        static JsonDocument imuDoc;
        imuDoc.clear();
        imuDoc["type"] = "imu";
        imuDoc["mac"] = nodeMacAddress;
        imuDoc["qw"] = qw;
        imuDoc["qx"] = qx;
        imuDoc["qy"] = qy;
        imuDoc["qz"] = qz;
        serializeJson(imuDoc, Serial);
        Serial.println();
      }
    }
    break;

  case SystemState::STATE_DIAG_RF:
    RfManager::update();
    break;

  default:
    break;
  }
}