#pragma once

#include <cstddef>
#include <cstdint>

namespace Crc32
{
    /// <summary>
    /// Continues an IEEE 802.3 CRC-32 calculation over the given bytes.
    /// The initial value should be 0xFFFFFFFF; the final value must be XORed
    /// with 0xFFFFFFFF to produce the standard result.
    /// </summary>
    uint32_t update(uint32_t crc, const uint8_t* data, size_t len);

    /// <summary>
    /// Computes the IEEE 802.3 / Ethernet CRC-32 of the given bytes.
    /// This matches System.IO.Hashing.Crc32 on the .NET host.
    /// </summary>
    uint32_t compute(const uint8_t* data, size_t len);
}
