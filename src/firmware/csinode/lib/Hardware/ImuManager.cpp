#include "ImuManager.h"
#include "config.h"

BNO08x ImuManager::_imu;
bool ImuManager::_imuHost = false;
bool ImuManager::_initialized = false;
bool ImuManager::_hasQuat = false;
float ImuManager::_quatW = 0.0f;
float ImuManager::_quatX = 0.0f;
float ImuManager::_quatY = 0.0f;
float ImuManager::_quatZ = 0.0f;

void ImuManager::begin()
{
    _imuHost = false;
    _initialized = false;
    _hasQuat = false;
    reset();
}

bool ImuManager::apply(bool imuHost)
{
    if (_imuHost == imuHost)
    {
        // Already in the requested state.
        return _initialized || !imuHost;
    }

    if (imuHost)
    {
        if (!initialize())
        {
            return false;
        }
    }
    else
    {
        reset();
    }

    _imuHost = imuHost;
    return true;
}

void ImuManager::update()
{
    if (!_initialized || !_imuHost)
    {
        return;
    }

    // Service the SHTP bus and cache any new rotation vector event.
    if (_imu.getSensorEvent())
    {
        if (_imu.getSensorEventID() == SENSOR_REPORTID_ROTATION_VECTOR)
        {
            float quatI = _imu.getQuatI();
            float quatJ = _imu.getQuatJ();
            float quatK = _imu.getQuatK();
            float quatReal = _imu.getQuatReal();

            // SparkFun BNO08x reports quaternion in i, j, k, real order.
            _quatX = quatI;
            _quatY = quatJ;
            _quatZ = quatK;
            _quatW = quatReal;
            _hasQuat = true;
        }
    }

    // If the sensor hub reports a self-reset, re-enable the rotation vector.
    if (_imu.wasReset())
    {
        _imu.enableRotationVector(10);
    }
}

bool ImuManager::tryGetQuaternion(float& w, float& x, float& y, float& z)
{
    if (!_hasQuat)
    {
        return false;
    }

    w = _quatW;
    x = _quatX;
    y = _quatY;
    z = _quatZ;
    _hasQuat = false;
    return true;
}

bool ImuManager::initialize()
{
    if (_initialized)
    {
        return true;
    }

    // Wire is already started during POST, but set a fast I2C clock.
    Wire.setClock(400000);

    // Use the default I2C address and no INT/RST control pins.
    if (!_imu.begin(Config::BNO085_ADDR_DEFAULT, Wire, -1, -1))
    {
        Serial.println("{\"type\":\"diag\",\"sensor\":\"bno085\",\"status\":\"init_failed\"}");
        return false;
    }

    if (!_imu.enableRotationVector(10))
    {
        Serial.println("{\"type\":\"diag\",\"sensor\":\"bno085\",\"status\":\"enable_failed\"}");
        return false;
    }

    _initialized = true;
    Serial.println("{\"type\":\"diag\",\"sensor\":\"bno085\",\"status\":\"init_ok\"}");
    return true;
}

void ImuManager::reset()
{
    _hasQuat = false;

    if (_initialized)
    {
        _imu.modeSleep();
        _initialized = false;
    }
}
