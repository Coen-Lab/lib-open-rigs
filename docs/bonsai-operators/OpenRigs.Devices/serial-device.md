# Serial Device

`SerialDevice` is the generic base for any non-Harp device that communicates over a serial port. The bundled Bonsai workflow opens a port with the configured parameters and exposes the raw byte/line stream on a subject, leaving message parsing to the consumer. Use it directly for ad-hoc serial hardware, or as a starting template for a more specific device module.

---

## Python schema

`SerialDevice` is defined in `open_rigs.devices`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `port_name` | `str` | - | Serial port the device is connected to (e.g. `COMx`) |
| `baud_rate` | `int` | `9600` | Baud rate for serial communication |
| `new_line` | `str` | `"\r\n"` | Line termination sequence used to delimit incoming messages |
| `read_buffer_size` | `int` | `4096` | Size, in bytes, of the read buffer |
| `write_buffer_size` | `int` | `2048` | Size, in bytes, of the write buffer |

Device-specific schemas (Arduino, lick spout stage, etc.) extend `SerialDevice` and add their own fields on top of these.

### Configuration example

`SerialDevice` is not a part of the lab rig model (`open_rigs.rigs.lab_rig.LabRig`), so a lab `rig.yml` with a `serialDevice` or similar key is refused. A project that needs one adds it to its own rig model, as the [hardware module tutorial](../../tutorials/hardware-modules.md) does, or a lab device takes it as a new part of `LabRig` (see [Building Blocks](../../articles/building-blocks.md)):

```python
from open_rigs.devices import SerialDevice

class Rig(...):
    ...
    sensor: SerialDevice = SerialDevice(
        port_name="COM8",
        baud_rate=115200,
    )
```

---

## Bonsai workflow

:::workflow
![SerialDevice](~/assets/workflows/devices/SerialDevice.svg){data-bonsai="~/src/OpenRigs.Devices/SerialDevice.bonsai"}
:::

The workflow opens the serial port with the externalized configuration values, publishes incoming lines on a named output subject, and writes any messages it receives on a named input subject back to the port. It is intended to be wrapped or extended for device-specific message parsing rather than used as-is for production data.
