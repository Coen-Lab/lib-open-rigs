# LicketySplit

The LicketySplit (`who_am_i = 1400`) is a Harp lick detector. It samples one or more capacitive/ADC channels and emits trigger events when the signal crosses configurable thresholds, providing low-latency lick timestamps on the Harp clock.

---

## Python schema

`LicketySplit` is defined in `open_rigs.devices` and extends `HarpDevice`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `port_name` | `str` | - | Serial port the device is connected to (e.g. `COM3`) |
| `channel0_trigger_threshold` | `Byte` | `0` | Channel 0 trigger threshold (% of baseline, board default 90), written at start; 0 writes nothing |
| `channel0_untrigger_threshold` | `Byte` | `0` | Channel 0 untrigger threshold (% of baseline, board default 98), written at start; 0 writes nothing |

Both registers are 8-bit percentages of the board's running baseline signal. A lick starts when the signal falls below the trigger threshold and ends when it rises above the untrigger threshold, so trigger must be below untrigger. A threshold of 0 is not written and the device keeps its own: writing 0 hangs the device about a second after start, with no events until it is power cycled. Set them only from readings taken on the rig.

### Configuration example

In your `rig.py`:

```python
from open_rigs.devices import LicketySplit

class Rig(...):
    ...
    lick_detector: LicketySplit = LicketySplit(
        port_name="COM7",
    )
```

Or as YAML:

```yaml
lick_detector:
  port_name: COM7
```

---

## Bonsai workflow

:::workflow
![LicketySplit](~/assets/workflows/devices/LicketySplit.svg){data-bonsai="~/src/OpenRigs.Devices/LicketySplit.bonsai"}
:::

The workflow opens the Harp serial connection, writes each threshold above 0 at its first event, and publishes the device event stream on a named `PublishSubject`. Downstream nodes can subscribe to that subject to extract the lick trigger events stamped against the Harp hardware clock.
