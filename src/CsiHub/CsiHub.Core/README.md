# CsiHub.Core

CsiHub.Core is the mathematical processing pipeline for CsiScope. It executes the MUSIC algorithm and FFT spectral analysis over continuous CSI phase streams, turning raw radio matrices into angle and vital-sign estimates.

## Respiration Filtering

Canine respiration is isolated with a dual-band filter applied to the FFT output:

- **Low-pass filter (0.15–0.5 Hz)** — tuned for resting breaths.
- **High-pass filter (1.5–5.0 Hz)** — tuned for heavy panting.

## Coordinate Transformations

World-frame coordinate transformations use SIMD-accelerated `Quaternion` and `Vector3` types, fusing BNO085 IMU orientation with antenna-array geometry.

## Dependencies

- `MathNet.Numerics` — complex subcarrier matrices, covariance construction, and Eigenvalue Decomposition (EVD).
- `FftSharp` — windowing functions and FFTs over continuous CSI phase streams.
