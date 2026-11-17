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

- `PulseController`: `output_pulse_enable` (the lines enabled) and `outputs`, one entry per line saying what drives its pulse width: a `FixedPulse` with `pulse_width_ms` set in the rig configuration, or a `CalibratedPulse` naming the calibration curve that converts a requested quantity, such as a reward volume, to a width at run time. DO1 to DO3 are always listed, an unused line a `FixedPulse` of width 0; DO0 and DOPort0 to DOPort2 only when used (a reward valve on one). A line left out of `output_pulse_enable` stays high once set until it is cleared, as a Zapit trigger line must (`open_rigs.rigs.lab_rig` refuses it pulse-enabled).
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
      DO1: { device: RewardValve, name: left }
      DO2: { device: RewardValve, name: right }
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
![BehaviorBoard](~/assets/workflows/devices/BehaviorBoard.svg){data-bonsai="~/src/OpenRigs.Devices/BehaviorBoard.bonsai"}
:::

`BehaviorBoard` is the board's device piece (see [Building Blocks](../../articles/building-blocks.md)): `Name` (`BehaviorBoard-<Entry>`) picks its entry under `behaviorBoard` in `rig.yml`, published as the `AsyncSubject` `<Name>`. It opens the board on its `portName` behind `<Name>.Data` and `<Name>.Commands` (a `BehaviorSubject`, so a per-trial graph that sends a command and completes leaves it open) and logs the board as `<Name>` once the experiment folder exists and the Harp clock is set. However the workflow stops, it clears the lines `wiring` gives a `ZapitTrigger` once the board has closed its port (`ClearOutputsOnStop`). The board's lines are driven by the line pieces below, each taking the board's `Name` as `Board`.

## Line pieces

### PulseOutputs

:::workflow
![PulseOutputs](~/assets/workflows/devices/PulseOutputs.svg){data-bonsai="~/src/OpenRigs.Devices/PulseOutputs.bonsai"}
:::

`PulseOutputs` (`PulseOutputs.bonsai`) is a board's pulsed lines: 0.5 s after the board's first message it closes DO1 to DO3 and the pulsed lines, then pulse-enables the lines `rig.yml`'s `outputPulseEnable` names without the ZapitTrigger's (`PulseEnabledLines`, `PulseSetupWrites`), then announces `PulsesEnabled`. It declares the board's `<Board>.TriggerCalibratedPulse` (`Tuple<string, int>`: a line such as `DO1` and the width in milliseconds) and fires each pulse sent into it (`CalibratedPulseWrites`): the width to that line's `PulseDO` or `PulseDOPort` register, then the line set, in that order on the board's command subject, so the board applies the new width before it fires. Any line the board can pulse takes one: DO0 to DO3 and DOPort0 to DOPort2. It stops the workflow at Start if a line `wiring` gives a `RewardValve` is not pulse-enabled, as a reward would then open the valve and leave it open. Each `RewardValve` piece sends its rewards through its own board's subject ([Building Blocks](../../articles/building-blocks.md#task-side-includes)); a calibration fires its own `FirePulses` into it.

### ZapitTriggerLine

`ZapitTriggerLine.bonsai` publishes the line `wiring` gives the `ZapitTrigger` as the `AsyncSubject` `ZapitTriggerLine`, which Zapit's trials hold high. It is never pulsed, and the board piece clears it at stop. Stops the workflow at Start unless exactly one line on the board has the `ZapitTrigger` and it is DO1, DO2 or DO3.

### CameraTriggerController

:::workflow
![CameraTriggerController](~/assets/workflows/devices/CameraTriggerController.svg){data-bonsai="~/src/OpenRigs.Devices/CameraTriggerController.bonsai"}
:::

Emits camera trigger pulses on `CameraOutput0` and `CameraOutput1` at the configured frequencies, started by `StartCameras`. The lab's `CameraTriggers` core include opens it with `rig.yml`'s `cameraTriggerController` and sends `StartCameras` once every camera has begun acquiring; each camera takes the trigger whose `CameraTrigger` wiring line names it (see [Triggered Spinnaker](triggered-spinnaker.md)).

### RunningWheel

:::workflow
![RunningWheel](~/assets/workflows/devices/RunningWheel.svg){data-bonsai="~/src/OpenRigs.Devices/RunningWheel.bonsai"}
:::

Once the board sends its first message, enables the encoder on port 2 in position mode and zeroes it, in that order, then publishes the distance run, in cm, on `WheelDistanceSubjectName`, from the board's `runningWheel.countsPerRev` and `runningWheel.wheelDiameterMm` in `rig.yml`.

### ClockFromBoard

:::workflow
![ClockFromBoard](~/assets/workflows/devices/ClockFromBoard.svg){data-bonsai="~/src/OpenRigs.Devices/ClockFromBoard.bonsai"}
:::

The rig's clock from the board's own Harp clock, for a rig with no timestamp generator; a workflow has exactly one clock. Sets the board's clock to the PC's UTC time and re-sends the write at each heartbeat until one agrees (`SetHarpClock`: a write is lost about one start in four), publishes `Timestamp` (the time of each of the board's `AnalogData` events, 1 kHz) and `HarpClockSet` (the first heartbeat within 2 s of UTC), and announces `HarpClockSet` into `ReadyStages`. Without it the board counts from power-on, and every Harp time and file name follows that count (1904). It also announces `HarpClockRead` at the board's first heartbeat. Stops the workflow, naming the board, if its clock is not set within 10 s of its first heartbeat, it sends no heartbeat within 30 s, or its heartbeats stop for 3 s.

`SetClock` (true by default) false reads the board's time without ever writing its clock, for the valve calibration on every rig with valves: on a rig with a timestamp generator the board follows the generator, on one without it runs on its own, and the calibration times its pulses only against each other and records nothing. `Timestamp` is then the board's time as it runs, Ready waits for `HarpClockRead` (set `ReadyGate`'s `PieceStages` to it in place of `HarpClockSet`), `HarpClockSet` comes only if the board already agrees with UTC, and only the 30 s and 3 s heartbeat stops remain. `workflow_lint` allows it only in a workflow with `NoExperimentFolder`.
