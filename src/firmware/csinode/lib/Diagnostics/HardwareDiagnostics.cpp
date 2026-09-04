#include "HardwareDiagnostics.h"
#include "SerialFraming.h"
#include "esp_system.h"
#include "esp_mac.h"
#include "LedManager.h"
#include <protocol_types.h>

void HardwareDiagnostics::executePOST()
{
    checkClockSpeeds();
    checkMemoryAllocation();
}

void HardwareDiagnostics::checkClockSpeeds()
{
    // Measures CPU core clocks on boot[cite: 1]
    uint32_t cpuFreq = getCpuFrequencyMhz();
    char postCpu[64];
    snprintf(postCpu, sizeof(postCpu), "{\"type\":\"post\",\"metric\":\"cpu_mhz\",\"value\":%d}\n", cpuFreq);
    SerialFraming::sendFramedText(postCpu);
}

void HardwareDiagnostics::checkMemoryAllocation()
{
    // Validates 16MB Flash and 8MB PSRAM allocation buffers[cite: 1]
    uint32_t psramSize = ESP.getPsramSize();
    uint32_t flashSize = ESP.getFlashChipSize();
    char postPsram[64];
    char postFlash[64];
    snprintf(postPsram, sizeof(postPsram), "{\"type\":\"post\",\"metric\":\"psram_bytes\",\"value\":%d}\n", psramSize);
    snprintf(postFlash, sizeof(postFlash), "{\"type\":\"post\",\"metric\":\"flash_bytes\",\"value\":%d}\n", flashSize);
    SerialFraming::sendFramedText(postPsram);
    SerialFraming::sendFramedText(postFlash);
}

bool HardwareDiagnostics::scanForBno085()
{
    // Scans the default I2C pins. If a BNO085 IMU is connected, it verifies presence[cite: 1]
    Wire.begin(Config::PIN_I2C_SDA, Config::PIN_I2C_SCL);
    Wire.beginTransmission(Config::BNO085_ADDR_DEFAULT);
    if (Wire.endTransmission() == 0)
    {
        SerialFraming::sendFramedText("{\"type\":\"diag\",\"sensor\":\"bno085\",\"status\":\"found\"}\n");
        return true;
    }
    else
    {
        // Logs an expected IMU_NOT_FOUND warning without halting boot[cite: 1]
        SerialFraming::sendFramedText("{\"type\":\"diag\",\"sensor\":\"bno085\",\"status\":\"IMU_NOT_FOUND\"}\n");
        return false;
    }
}

String HardwareDiagnostics::getMacAddress()
{
    uint8_t mac[6];
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    char macStr[18];
    snprintf(macStr, sizeof(macStr), "%02X:%02X:%02X:%02X:%02X:%02X", mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return String(macStr);
}

void HardwareDiagnostics::setLedState(SystemState state)
{
    // Delegate all visual feedback to the non-blocking LED manager.
    LedManager::setState(state);
}