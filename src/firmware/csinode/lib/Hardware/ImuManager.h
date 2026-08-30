#pragma once

#include <Arduino.h>
#include <Wire.h>
#include "SparkFun_BNO08x_Arduino_Library.h"

/// <summary>
/// Lazily initializes and polls the BNO085 IMU for rotation vector data.
/// All operations are non-blocking; begin() and update() are driven by the main loop.
/// </summary>
class ImuManager
{
public:
    static void begin();

    /// <summary>
    /// Call each loop() pass to service the I2C bus and cache the latest quaternion.
    /// </summary>
    static void update();

    /// <summary>
    /// Enable or disable IMU hosting. Lazy initialization happens on first true transition.
    /// Returns true if the requested state is reached (or already active).
    /// </summary>
    static bool apply(bool imuHost);

    /// <summary>
    /// Returns true when a fresh quaternion is available for the streaming loop.
    /// </summary>
    static bool tryGetQuaternion(float& w, float& x, float& y, float& z);

    /// <summary>
    /// Returns the current IMU host flag.
    /// </summary>
    static bool isHost() { return _imuHost && _initialized; }

private:
    static BNO08x _imu;
    static bool _imuHost;
    static bool _initialized;
    static bool _hasQuat;
    static float _quatW;
    static float _quatX;
    static float _quatY;
    static float _quatZ;

    static bool initialize();
    static void reset();
};
