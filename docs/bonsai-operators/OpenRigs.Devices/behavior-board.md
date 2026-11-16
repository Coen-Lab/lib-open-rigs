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

- `PulseController`: `output_pulse_enable` (the `DO1`/`DO2`/`DO3` lines enabled) and `outputs`, one entry per line saying what drives its pulse width: a `FixedPulse` with `pulse_width_ms` set in the rig configuration, or a `CalibratedPulse` naming the calibration curve that converts a requested quantity, such as a reward volume, to a width at run time. All three lines are listed; an unused line is a `FixedPulse` of width 0. A line left out of `output_pulse_enable` stays high once set until it is cleared, as a Zapit trigger line must (`open_rigs.rigs.lab_rig` refuses it pulse-enabled).
- `CameraTriggerController`: `trigger0_frequency` and `trigger1_frequency` (Hz) for `CameraOutput0` and `CameraOutput1`.
- `RunningWheel`: `counts_per_rev` and `wheel_diameter_mm`.

### Configuration example

In `rig.yml`, under `behaviorBoard`, one entry per board: the entry's name is the piece's (`main` is `BehaviorBoard-Main`). The lab rig model, `open_rigs.rigs.lab_rig.LabRig`, reads it; a key it lacks is refused.

```yaml
behaviorBoard:
  main:
    portName: COM5
    usbSerial: AB0M7PLB
    pulseController:
      outputPulseEnable: "DO1, DO2"
      outputs:
        DO1: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO1.json }
        DO2: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO2.json }
        DO3: { discriminatorType: FixedPulse, pulseWidthMs: 0 }
    wiring:
      DO0: { device: CameraTrigger, targets: [Camera-Face] }
      DO1: { device: RewardValve, side: left }
      DO2: { device: RewardValve, side: right }
      DO3: { device: ZapitTrigger }
    cameraTriggerController:
      trigger0Enabled: true
      trigger0Frequency: 50
      trigger1Enabled: false
      trigger1Frequency: 50
    runningWheel:
      wheelDiameterMm: 75
      countsPerRev: 4096
```

In Python the same board is `LabRig(behavior_board={"main": BehaviorBoard(port_name="COM5", ...)})`; `rig-template.yml` (`open_rigs\rigs`) is the whole file with every part.

---

## Bonsai workflow

:::workflow
![BehaviorBoard](~/assets/workflows/devices/BehaviorBoard/BehaviorBoard.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/BehaviorBoard.bonsai"}
:::

`BehaviorBoard` is the board's device piece (see [Building Blocks](../../articles/building-blocks.md)): `Name` (`BehaviorBoard-<Entry>`) picks its entry under `behaviorBoard` in `rig.yml`, published as the `AsyncSubject` `<Name>`. It opens the board on its `portName` behind `<Name>.Data` and `<Name>.Commands` (a `BehaviorSubject`, so a per-trial graph that sends a command and completes leaves it open) and logs the board as `<Name>` once the experiment folder exists and the Harp clock is set. However the workflow stops, it clears the lines `wiring` gives a `ZapitTrigger` once the board has closed its port (`ClearOutputsOnStop`). The board's lines are driven by the line pieces below, each taking the board's `Name` as `Board`.

## Line pieces

### PulseController and PulseOutputs

:::workflow
![PulseController](~/assets/workflows/devices/BehaviorBoard/PulseController.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/PulseController.bonsai"}
:::

Generates pulses on the digital output lines listed in `output_pulse_enable`. Used to drive valves and other on/off actuators in response to commands on its input subjects.

Two trigger subjects, one per kind of line:

- `TriggerPulse` (`int`, the DO line) for a `FixedPulse` line. Its width is written once at startup from the `PulseDO1`, `PulseDO2` and `PulseDO3` properties, which the rig maps from its configuration, and each trigger only sets the line.
- `TriggerCalibratedPulse` (`Tuple<int, int>`, the DO line 1 to 3 and the width in milliseconds) for a `CalibratedPulse` line. Each trigger writes the width to that line's `PulseDO` register and then sets the line, in that order on the same command subject, so the board applies the new width before it fires. The width comes from the calibration curve lookup in `OpenRigs.Core` at trial time; nothing about it is stored in the rig configuration or the workflow.

Both subject names are externalized, so a rig can rename them to match its own.

`PulseOutputs` (`BehaviorBoard.PulseOutputs.bonsai`) is the piece around it: it pulse-enables the lines `rig.yml`'s `outputPulseEnable` names without the ZapitTrigger's (`PulseEnabledLines`), and stops the workflow at Start if a line `wiring` gives a `RewardValve` is not pulse-enabled, as a reward would then open the valve and leave it open. `DeliverReward` and `ManualRewards` send rewards through it ([Building Blocks](../../articles/building-blocks.md#task-side-includes)).

### ZapitTriggerLine

`BehaviorBoard.ZapitTriggerLine.bonsai` publishes the line `wiring` gives the `ZapitTrigger` as the `AsyncSubject` `ZapitTriggerLine`, which Zapit's trials hold high. It is never pulsed, and the board piece clears it at stop. Stops the workflow at Start unless exactly one line on the board has the `ZapitTrigger` and it is DO1, DO2 or DO3.

### CameraTriggerController

:::workflow
![CameraTriggerController](~/assets/workflows/devices/BehaviorBoard/CameraTriggerController.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/CameraTriggerController.bonsai"}
:::

Emits camera trigger pulses on `CameraOutput0` and `CameraOutput1` at the configured frequencies, started by `StartCameras`. The lab's `CameraTriggers` core include opens it with `rig.yml`'s `cameraTriggerController` and sends `StartCameras` once every camera has begun acquiring; each camera takes the trigger whose `CameraTrigger` wiring line names it (see [Triggered Spinnaker](triggered-spinnaker.md)).

### RunningWheel

:::workflow
![RunningWheel](~/assets/workflows/devices/BehaviorBoard/RunningWheel.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/RunningWheel.bonsai"}
:::

Once the board sends its first message, enables the encoder on port 2 in position mode and zeroes it, in that order, then publishes the distance run, in cm, on `WheelDistanceSubjectName`, from the board's `runningWheel.countsPerRev` and `runningWheel.wheelDiameterMm` in `rig.yml`.

### ClockFromBoard

:::workflow
![ClockFromBoard](~/assets/workflows/devices/BehaviorBoard/ClockFromBoard.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard/ClockFromBoard.bonsai"}
:::

The rig's clock from the board's own Harp clock, for a rig with no timestamp generator; a workflow has exactly one clock. Sets the board's clock to the PC's UTC time and re-sends the write at each heartbeat until one agrees (`SetHarpClock`: a write is lost about one start in four), publishes `Timestamp` (the time of each of the board's `AnalogData` events, 1 kHz) and `HarpClockSet` (the first heartbeat within 2 s of UTC), and announces `HarpClockSet` into `ReadyStages`. Without it the board counts from power-on, and every Harp time and file name follows that count (1904). Stops the workflow, naming the board, if its clock is not set within 10 s or its heartbeats stop for 3 s.
