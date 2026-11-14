# Log Harp Device

`LogHarpDevice` logs the raw binary message stream from a Harp device. The incoming stream is split by message type: `Read` messages are written as raw bytes to a binary `.bin` file, while `Event` messages are demultiplexed by register address into separate timestamped `.bin`files. 


---

## Bonsai workflow

:::workflow
![LogHarpDevice](~/assets/workflows/logging/LogHarpDevice.svg){data-bonsai="~/src/OpenRigs.Logging/LogHarpDevice.bonsai"}
:::

The operator has two parallel outputs:

- **LogHarp** - writes all `Read` messages as raw bytes to a single `.bin` file.
- **LogHarpDemux** - groups messages by register address and writes a separate timestamped matrix `.bin` file per register, each suffixed with the register address.

File paths are assembled from the `PathPrefix` subject (published by [LogController](log-controller.md)) and the `LogName` externalized property.

Each file is written by `BinaryLogWriter`, on its own thread, as Bonsai's `MatrixWriter` did, so a slow disk never holds up the device. Unlike `MatrixWriter`'s, that thread keeps Bonsai open until the file is closed: headless Bonsai (`--no-editor`) exits as soon as the workflow ends, and with `MatrixWriter` a log could lose its last messages, or never be created.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `LogName` | - | Stem added after the path prefix (e.g. `behavior`) |

---

## Usage

Connect the events subject of any Harp device to `LogHarpDevice` and set `LogName`.

The operator writes two sets of files under the path assembled by [LogController](log-controller.md):

- `{PathPrefix}\behavior\behavior.bin`. Raw `Read` message bytes.
- `{PathPrefix}\behavior\behavior_{register}.bin`. One column-major matrix file per register address, containing the `Event` stream for that register. The resulting `.bin` files can be loaded in Python with the `harp-python` package (`harp.read()`).
