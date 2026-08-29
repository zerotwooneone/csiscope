#pragma once

enum class SystemState
{
    STATE_BOOT,
    STATE_STANDBY,
    STATE_ASSIGNED,
    STATE_STREAMING,
    STATE_DIAG_SYNC,
    STATE_DIAG_IMU,
    STATE_DIAG_RF
};

enum class NodeRole
{
    NONE,
    LEADER,
    FOLLOWER
};