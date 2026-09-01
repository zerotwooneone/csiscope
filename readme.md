# CsiScope

CsiScope is an experimental ambient sensing initiative designed to explore non-optical, privacy-preserving tracking of canine presence, posture, and micro-physiological states (such as resting and breathing patterns) inside a home environment[cite: 3]. By leveraging commodity Wi-Fi Channel State Information (CSI) processed through a custom-built, portable L-shaped hardware array, the project aims to detect local physical disruptions without relying on cameras, microphones, or wearables[cite: 3].

## Core Objectives

*   **Ambient Spatial Awareness:** Detect and track the presence and general positioning of a pet within targeted indoor zones using radio frequency reflections[cite: 3].
*   **Micro-Movement & Vital Monitoring:** Capture subtle physiological indicators, such as breathing rhythms and rest states, through fine-grained signal variance analysis[cite: 3].
*   **Portable and Flexible Deployment:** Utilize a rigid, handheld L-shaped physical array that can be positioned on surfaces or pointed toward specific resting areas for localized scanning[cite: 3].
*   **Privacy-First Architecture:** Ensure absolute domestic privacy by relying entirely on mathematical disruptions to local RF fields rather than optical or audio surveillance[cite: 3].

## System Architecture

The project relies on a strictly partitioned computational model:

*   **ESP32-S3 Microcontroller Array:** A 3-node array of wireless microcontrollers running a unified firmware[cite: 3, 4]. The nodes extract raw CSI I/Q matrices, tag data with hardware interrupt timestamps, manage the BNO085 I2C sensor, and serialize payloads into compact Newline Delimited JSON (NDJSON)[cite: 4].
*   **Central Brain (.NET 10):** A unified .NET 10 runtime environment hosts a Blazor Server application to orchestrate the array[cite: 4]. This host manages CSI Ratio phase sanitization, MUSIC algorithm Eigenvalue Decomposition for Angle of Arrival (AoA), FFT spectral analysis, and IMU world-frame coordinate transformations[cite: 4].

## Operational Modes

To accommodate the physics of RF sensing, the system is divided into two distinct operational modes[cite: 3]:

1.  **Handheld Directional Tracking:** Uses active IMU fusion for spatial orientation while sweeping the array across a room[cite: 3]. 
2.  **Stationary Vital Sign Analysis:** Used for detecting micro-movements like canine breathing[cite: 3]. The array must be perfectly stable (e.g., resting flat) because human hand tremors will entirely swamp the tiny phase shifts caused by a dog's chest moving[cite: 3]. The IMU acts as a vibration gatekeeper, automatically pausing respiration calculations if physical motion is detected[cite: 2].

## Hardware Requirements

*   **Microcontrollers:** 3x ESP32-S3 Development Boards with IPEX/U.FL Connectors[cite: 1].
*   **Antennas:** 3x 2.4GHz / 5GHz Dual-Band Omni-Directional Antennas mounted at precise 6.25 cm intervals ($\lambda/2$ for 2.4 GHz)[cite: 1, 2].
*   **IMU:** 1x BNO085 9-axis IMU (wired exclusively to the Leader node)[cite: 1, 2].
*   **Connectivity:** 4-Port USB 3.0 Hub for serial data aggregation[cite: 1].
*   **Sync Wiring:** A hardwired Trigger signal (GPIO output to GPIO inputs) and Common Ground line between nodes to bypass unpredictable Wi-Fi scheduling latencies[cite: 1, 2].