#include <Arduino.h>
#include <Wire.h>
#include <SPI.h>
#include <ArduinoJson.h>
#include "config.h"
#include "protocol_types.h"
#include "HardwareDiagnostics.h"
#include "ImuManager.h"
#include "LedManager.h"
#include "RfManager.h"
#include "SerialFraming.h"
#include "SerialManager.h"
#include "SyncManager.h"

// Global State
SystemState currentState = SystemState::STATE_BOOT;
String nodeMacAddress;
unsigned long lastHeartbeatMs = 0;

void setup()
{
  // Increase the USB-CDC TX ring buffer so large CSI payloads fit without
  // partial writes, and force non-blocking writes (no delay() in the CSI ISR).
  Serial.setTxBufferSize(8192);
  Serial.setTxTimeoutMs(0);

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
  SerialFraming::sendFramedJson(bootDoc);

  HardwareDiagnostics::executePOST();
  HardwareDiagnostics::scanForBno085();

  // Transition to Standby
  currentState = SystemState::STATE_STANDBY;
  HardwareDiagnostics::setLedState(currentState);

  // Announce the node's identity and capabilities to the host.
  SerialManager::sendConfig();
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

      SerialFraming::sendFramedJson(hbDoc);
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
        SerialFraming::sendFramedJson(imuDoc);
      }
    }
    break;

  case SystemState::STATE_DIAG_SYNC:
  {
    static unsigned long lastDiagSyncMs = 0;
    static unsigned long lastDiagHeartbeatMs = 0;

    if (currentMs - lastDiagHeartbeatMs >= Config::HEARTBEAT_INTERVAL_MS)
    {
      lastDiagHeartbeatMs = currentMs;

      static JsonDocument hbDoc;
      hbDoc.clear();
      hbDoc["type"] = "hb";
      hbDoc["mac"] = nodeMacAddress;
      hbDoc["state"] = SerialManager::stateToString(currentState);
      hbDoc["uptime"] = millis() / 1000;
      hbDoc["bw"] = Config::CSI_BANDWIDTH;
      hbDoc["clock_leader"] = SyncManager::isLeader();
      hbDoc["imu_host"] = ImuManager::isHost();

      SerialFraming::sendFramedJson(hbDoc);
    }

    if (currentMs - lastDiagSyncMs >= 250)
    {
      lastDiagSyncMs = currentMs;

      uint32_t pulseCount = 0;
      double latencyUs = 0.0;
      double jitterUs = 0.0;
      SyncManager::getDiagnosticSnapshot(pulseCount, latencyUs, jitterUs);

      JsonDocument diagDoc;
      diagDoc["type"] = "diag";
      diagDoc["mac"] = nodeMacAddress;
      diagDoc["test"] = "sync";
      diagDoc["pulse_count"] = pulseCount;
      diagDoc["latency_us"] = latencyUs;
      diagDoc["jitter_us"] = jitterUs;
      SerialFraming::sendFramedJson(diagDoc);
    }
    break;
  }

  case SystemState::STATE_DIAG_RF:
    RfManager::update();
    break;

  default:
    break;
  }
}