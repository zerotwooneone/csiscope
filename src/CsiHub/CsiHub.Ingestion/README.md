# CsiHub.Ingestion

CsiHub.Ingestion is the decoupled data-ingestion layer for CsiScope. It receives CSI radio matrices and BNO085 IMU orientation frames asynchronously at the background service layer.

## Streaming Model

- Uses `System.IO.Pipelines` for high-throughput, low-allocation parsing of serial and network streams.
- Uses `System.Threading.Channels` to decouple 50–100 Hz ingestion from the downstream DSP pipeline.

## Payload Format

Nodes emit highly compact, flat-array Newline Delimited JSON (NDJSON) payloads. The ingestion layer parses these streams without blocking the main host.

## Fault Tolerance

Node disconnections are surfaced as state events rather than fatal exceptions. The UI degrades gracefully when a node drops off the array, allowing the operator to reconnect without restarting the application.
