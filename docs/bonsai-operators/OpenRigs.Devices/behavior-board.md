# Behavior Board

The Behavior Board (`who_am_i = 1216`) is a Harp board commonly used as an I/O hub on Open Rigs. It bundles three optional modules: a pulse controller for valves and other digital outputs, a camera trigger controller, and a running-wheel encoder reader. Each of these modules can be enabled independently per rig.

---

## Python schema

`BehaviorBoard` is defined in `open_rigs.devices` and extends `HarpBehavior`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `port_name` | `str` | - | Serial port the device is connected to (e.g. `COM3`) |
| `pulse_controller` | `PulseController \| None` | `None` | Optional module for generating digital output pulses (e.g. valve opens) |
| `camera_trigger_controller` | `CameraTriggerController \| None` | `None` | Optional module for emitting camera trigger pulses |
| `running_wheel` | `RunningWheel \| None` | `None` | Optional running wheel geometry, used to convert encoder counts to speed and distance |

Sub-modules:

- `PulseController`: `output_pulse_enable` (the `DO1`/`DO2`/`DO3` lines enabled) and `outputs`, one entry per line saying what drives its pulse width: a `FixedPulse` with `pulse_width_ms` set in the rig configuration, or a `CalibratedPulse` naming the calibration curve that converts a requested quantity, such as a reward volume, to a width at run time. All three lines are listed; an unused line is a `FixedPulse` of width 0.
- `CameraTriggerController`: `trigger0_frequency` and `trigger1_frequency` (Hz) for `CameraOutput0` and `CameraOutput1`.
- `RunningWheel`: `counts_per_rev` and `wheel_diameter_mm`.

### Configuration example

In your `rig.py`:

```python
from open_rigs.devices import (
    BehaviorBoard,
    CalibratedPulse,
    CameraTriggerController,
    FixedPulse,
    PulseController,
    PulseOutput,
    PulseOutputs,
    RunningWheel,
)

class Rig(...):
    ...
    behavior_board: BehaviorBoard = BehaviorBoard(
        port_name="COM5",
        pulse_controller=PulseController(
            outputs=PulseOutputs(
                DO1=PulseOutput(CalibratedPulse(artefact="calibration/valve-DO1.json")),
                DO2=PulseOutput(CalibratedPulse(artefact="calibration/valve-DO2.json")),
                DO3=PulseOutput(FixedPulse(pulse_width_ms=5)),
            ),
        ),
        camera_trigger_controller=CameraTriggerController(
            trigger0_frequency=50,
            trigger1_frequency=50,
        ),
        running_wheel=RunningWheel(counts_per_rev=1024, wheel_diameter_mm=200),
    )
```

---

## Bonsai workflow

:::workflow
![BehaviorBoard](~/assets/workflows/devices/BehaviorBoard/BehaviorBoard.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/BehaviorBoard.bonsai"}
:::

The top-level workflow opens the Harp serial connection, publishes the raw event stream on a named subject, and routes events into the nested sub-workflows below. Each sub-workflow exposes its own subject names so downstream nodes can subscribe to just the streams they need.

## Sub-operators

Sub-operators are nested workflows within the Behavior Board that interface with the device to configure and expose specific hardware functions. Each sub-operator subscribes to the shared event stream published by the top-level workflow and routes commands or data to its designated hardware module. They can be enabled or disabled independently via the Python schema, and each exposes its own named subjects so downstream nodes can subscribe to only the streams they need.

### PulseController

:::workflow
![PulseController](~/assets/workflows/devices/BehaviorBoard/PulseController.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/PulseController.bonsai"}
:::

Generates pulses on the digital output lines listed in `output_pulse_enable`. Used to drive valves and other on/off actuators in response to commands on its input subjects.

Two trigger subjects, one per kind of line:

- `TriggerPulse` (`int`, the DO line) for a `FixedPulse` line. Its width is written once at startup from the `PulseDO1`, `PulseDO2` and `PulseDO3` properties, which the rig maps from its configuration, and each trigger only sets the line.
- `TriggerCalibratedPulse` (`Tuple<int, int>`, the DO line 1 to 3 and the width in milliseconds) for a `CalibratedPulse` line. Each trigger writes the width to that line's `PulseDO` register and then sets the line, in that order on the same command subject, so the board applies the new width before it fires. The width comes from the calibration curve lookup in `OpenRigs.Core` at trial time; nothing about it is stored in the rig configuration or the workflow.

Both subject names are externalized, so a rig can rename them to match its own.

### CameraTriggerController

:::workflow
![CameraTriggerController](~/assets/workflows/devices/BehaviorBoard/CameraTriggerController.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/CameraTriggerController.bonsai"}
:::

Emits camera trigger pulses on `CameraOutput0` and `CameraOutput1` at the configured frequencies. Pair with a triggered camera module (such as [Triggered Spinnaker](triggered-spinnaker.md)) to align video to the Harp clock.

### RunningWheel

:::workflow
![RunningWheel](~/assets/workflows/devices/BehaviorBoard/RunningWheel.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/RunningWheel.bonsai"}
:::

Reads the rotary encoder and converts counts into wheel speed and distance using `counts_per_rev` and `wheel_diameter_mm` from the rig configuration.

### Timestamps

:::workflow
![Timestamps](~/assets/workflows/devices/BehaviorBoard/Timestamps.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/Timestamps.bonsai"}
:::

Derives a hardware-clock timebase from the Behavior Board's event stream and publishes it on a named subject. Other modules can use `WithLatestFrom` against this subject to stamp their own data with the Harp clock, keeping all rig data on a single timebase.
