# LicketySplit

The LicketySplit (`who_am_i = 1400`) is a Harp lick detector. It samples one or more capacitive/ADC channels and emits trigger events when the signal crosses configurable thresholds, providing low-latency lick timestamps on the Harp clock.

---

## Python schema

`LicketySplit` is defined in `open_rigs.devices` and extends `HarpDevice`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `port_name` | `str` | - | Serial port the device is connected to (e.g. `COM3`) |
| `channel0_trigger_threshold` | `UShort` | `0` | ADC threshold above which Channel 0 triggers a lick |
| `channel0_untrigger_threshold` | `UShort` | `0` | ADC threshold below which Channel 0 untriggers a lick |

### Configuration example

In your `rig.py`:

```python
from open_rigs.devices import LicketySplit

class Rig(...):
    ...
    lick_detector: LicketySplit = LicketySplit(
        port_name="COM7",
        channel0_trigger_threshold=1200,
        channel0_untrigger_threshold=900,
    )
```

Or as YAML:

```yaml
lick_detector:
  port_name: COM7
  channel0_trigger_threshold: 1200
  channel0_untrigger_threshold: 900
```

---

## Bonsai workflow

:::workflow
![LicketySplit](~/assets/workflows/devices/LicketySplit.svg){data-bonsai="~/src/OpenRigs.Devices/LicketySplit.bonsai"}
:::

`LicketySplit` is a device piece (see [Building Blocks](../../articles/building-blocks.md)): `Name` (`LicketySplit-<Entry>`) picks its entry under `licketySplit` in `rig.yml`, published as the `AsyncSubject` `<Name>`. It opens the device on its `portName` behind `<Name>.Data` and `<Name>.Commands`, writes its channel 0 trigger and untrigger thresholds at its first message, publishes the lick state (true while a lick is detected) on `LickStateSubjectName`, and logs the device as `<Name>` once the experiment folder exists and the Harp clock is set. Stops the workflow at Start, naming the entries `rig.yml` has, if it has none by that name.
