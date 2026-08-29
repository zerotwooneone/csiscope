# CsiHub.Web

CsiHub.Web is the Blazor Server orchestration surface for the CsiScope central computing hub.

## Views

- **High-Level View** — Presents abstract spatial tracking and physiological states of the monitored subject in an operator-friendly form.
- **Low-Level View** — Acts as an engineering dashboard, exposing raw CSI subcarriers, MUSIC Angle-of-Arrival spectra, FFT bins, and BNO085 IMU orientation frames.

## Rendering Pipeline

The application aggregates DSP buffers and downsamples them to a **3 Hz dispatch rate** before pushing updates to the browser. This eliminates WebSocket rendering bottlenecks that would occur if the full 50–100 Hz sensor stream were forwarded directly into the Blazor circuit.
