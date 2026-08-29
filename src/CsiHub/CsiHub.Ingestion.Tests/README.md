# CsiHub.Ingestion.Tests

This xUnit project tests the decoupled data-ingestion pipeline for CsiScope's two distinct operational modes:

1. **Handheld Directional Tracking** — streaming high-rate CSI and BNO085 orientation frames while the array is in motion.
2. **Stationary Vital Sign Analysis** — streaming stable, high-rate data for micro-movement analysis.

## Coverage

- Parsing highly compact, flat-array Newline Delimited JSON (NDJSON) payloads.
- Channel back-pressure and producer/consumer ordering at 50–100 Hz.
- Fault-tolerant reader behavior for malformed or truncated frames.
- IMU stability gating integration, ensuring respiration calculations automatically pause when physical motion is detected so the vital-sign output does not produce false readings.
