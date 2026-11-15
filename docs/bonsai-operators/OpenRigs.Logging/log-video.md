# Log Video

`LogVideo` writes a camera stream to disk as an AVI video file and an accompanying CSV metadata file. Frames are grouped into chunks, compressed with the FMP4 codec, and written alongside a row-per-frame metadata file that records the Harp timestamp, frame ID, exposure time, and gain for every frame. This keeps video and electrophysiology data on the same clock for offline alignment.

---

## Bonsai workflow

:::workflow
![LogVideo](~/assets/workflows/logging/LogVideo.svg){data-bonsai="~/src/OpenRigs.Logging/LogVideo.bonsai"}
:::

The workflow groups incoming frames by time (`GroupByTime` with `ChunkSize = 1`), then writes two parallel outputs:

- **VideoWriter** - encodes frames to `.avi` using the FMP4 codec at the configured `FrameRate`.
- **CsvWriter** - records per-frame metadata (timestamp, frame ID, exposure, gain) to a `.csv` file.

File paths are built from the `PathPrefix` subject (published by [LogController](log-controller.md)) plus the `LogName` property.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `LogName` | - | Stem added after the path prefix (e.g. `_camera0`) |
| `FrameRate` | `50` | Nominal frame rate written into the AVI container header |

### Metadata columns

The CSV file contains one row per frame with these columns:

| Column | Description |
|--------|-------------|
| `Seconds` | Harp hardware timestamp |
| `FrameID` | Monotonically increasing frame counter from the camera |
| `Timestamp` | Camera-internal timestamp |
| `ExposureTime` | Actual exposure duration in microseconds |
| `Gain` | Analogue gain applied by the camera |

---

## Usage

Connect a camera stream (i.e. from [Triggered Spinnaker](../OpenRigs.Devices/triggered-spinnaker.md)) to `LogVideo`:


One `LogVideo` instance is needed per camera. The `FrameRate` parameter controls the AVI container header only; the actual frame rate is determined by the camera trigger.