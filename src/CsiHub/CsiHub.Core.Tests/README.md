# CsiHub.Core.Tests

This xUnit project validates the logic required for CsiScope's two distinct operational modes:

1. **Handheld Directional Tracking** — MUSIC-based Angle of Arrival and spatial correlation while the array is swept.
2. **Stationary Vital Sign Analysis** — FFT-based respiration and micro-movement detection while the array is stable.

## Coverage

- Phase sanitization and unwrapping of CSI subcarrier streams.
- Covariance construction and Eigenvalue Decomposition for the MUSIC algorithm.
- Spatial correlation and peak extraction for AoA estimation.
- Dual-band vital-sign DSP, including IMU stability gating so respiration calculations automatically pause when physical motion is detected, preventing false readings.
