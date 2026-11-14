# Dome Building Blocks

A dome rig is a Behavior board, a timestamp generator, a two-driver lick spout stage, two lick detectors, triggered cameras, a projector and a speaker array. Every workflow that runs on one reads the same `rig.yml`, modelled by `open_rigs.rigs.dome.DomeRig`, and builds on the blocks below, so each workflow keeps only its task. This page lists each block: what it takes, what it gives and where it lives.

## The rig model and its wiring

`open_rigs.rigs.dome.DomeRig` (`src/open_rigs/rigs/dome.py`) is the whole of a dome's `rig.yml`. A workflow's own rig model is this class, and its generated C# reads the file. Reward valves and the Zapit trigger are found from the Behavior board's `wiring`, which is required:

```yaml
behaviorBoard:
  main:
    portName: COM3
    usbSerial: AB0M7PLB
    pulseController:
      outputPulseEnable: "DO1, DO2"
      outputs:
        DO1: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO1.json }
        DO2: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO2.json }
        DO3: { discriminatorType: FixedPulse, pulseWidthMs: 0 }
    wiring:
      DO0: { device: CameraTrigger, targets: [Camera-Face, Camera-Pupil, Camera-Body] }
      DO1: { device: RewardValve, side: left }
      DO2: { device: RewardValve, side: right }
      DO3: { device: ZapitTrigger, note: also the masking light }
```

The model refuses a `rig.yml` unless each board's wiring has exactly one `RewardValve` per side, each with its `side`, each on a line whose output is a `CalibratedPulse`, and exactly one `ZapitTrigger`, on a line not in `outputPulseEnable` (a pulse-enabled line ends itself after its `PulseDOx` width, and the Zapit line, with the masking light on it, is held from the trigger until the workflow clears it; its `outputs` entry stays, a `FixedPulse` of width 0); every refusal ends "(dome rig model, open_rigs.rigs.dome)". It also refuses a `camera` list of other than three entries (`face`, `pupil`, `body`). Other required keys: `speakerArray.errorSoundChannel` (beside `device.bootDelay`, `syncChannel` and `syncGain`), `lickSpoutDefaultPosition` (the in position of a mouse with no positions file) and `zapit` (`host`, `port`, `hardwareTriggered`).

## Blocks

| Block | File | Takes | Gives |
|---|---|---|---|
| `DomeHardware` | `OpenRigs.Devices:DomeHardware.bonsai` | subjects `RigConfigs` (the workflow's deserialised `rig.yml`), `RigConfigDirectory`, `SubjectId`; property `SpoutStepSize` | an `AsyncSubject` per device named as in the data (`BehaviorBoard-Main`, `TimestampGeneratorGen3-Main`, `StepperDriver-A`, `StepperDriver-B`, `LicketySplit-Left`, `LicketySplit-Right`, `Camera-Face`, `Camera-Pupil`, `Camera-Body`), each opened behind `<Name>.Data` (and `<Name>.Commands` for Harp devices); the cameras run on the board's trigger 0 with rig.yml's serial, exposure, gain, binning and pixel format, and stop the workflow at start if rig.yml lists other than three or an exposure does not fit the trigger period (`ExposureFitsTrigger`); `Timestamp` (the Harp clock), `HarpClockSet`, `RigFile`, `ZapitTriggerLine`, `WheelDistanceCm`, `LickStateLeft`, `LickStateRight`; takes `TriggerCalibratedPulse` and `TriggerPulse`; includes `HarpLickSpoutStage` |
| `HarpLickSpoutStage` | `OpenRigs.Devices:HarpLickSpoutStage.bonsai` | subjects `StepperDriver-A`, `StepperDriver-B`, `ControlEvents`, `LickSpoutStage.Commands`; properties `RigFile`, `PositionsFile`, `StepSize` | opens both drivers, sets them up, homes all five motors and moves the spouts in (`LickSpoutStage.Homed`); each target on `LickSpoutStage.Target`; handles the commands `SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SaveSpoutPositions`, `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftFront`, `LeftBack`, `RightFront`, `RightBack`, `BaseLeft`, `BaseRight`, which the workflow declares on `ExperimentControl` |
| `DomeHarpLogs` | `OpenRigs.Devices:DomeHarpLogs.bonsai` | `<Name>.Data` of each Harp device, `PathPrefix`, `HarpClockSet` | one `LogHarpDevice` per device, `LogName` its name, started once the experiment folder exists and the Harp clock is set. Place it after `PathPrefix` is declared |
| `DeliverReward` | `OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai` | input `(side, microlitres)`; properties `RigFile`, `CalibrationDirectory`, `MissingCalibration`; subject `Timestamp` | `RewardLineLeft`, `RewardLineRight` (`LoadRewardLine`), one plan per reward on `RewardDelivered`, its pulses on `TriggerCalibratedPulse` one reward after another; a `RewardDelivered` row on `AsyncEvents` (Value the microlitres delivered, Detail starting with the side) and each calibrated line's file as `ValveCalibrationDO<n>`. Place it after `AsyncEvents` and `PathPrefix` are declared |
| `DomeDisplay` | `OpenRigs.Vision:DomeDisplay.bonsai` | input `projectionCalibration`; properties `DisplayDevice`, `WindowState`, `Width`, `Height` | the BonVision window, mesh mapping and gamma correction; stimuli draw on `DrawStimuli`; disposed on the render thread |
| `BehaviorBoard.RunningWheel` | `OpenRigs.Devices:BehaviorBoard.RunningWheel.bonsai` | the board's subjects, `CountsPerRev`, `WheelDiameterMm` | the distance run, in cm, with the encoder in Position mode |

## Operators

| Operator | Package | What it does |
|---|---|---|
| `LickSpoutStage`, `SetUpLickSpoutDriver`, `HomeLickSpoutMotor`, `MoveLickSpoutDriver`, `ToLickSpoutCommand`, `SpoutPositions` | Devices | the stage's state, setup, homing and moves; a mouse's positions file is one line, `baseTransverse,leftRadial,rightRadial,leftElevation,rightElevation` |
| `WiredOutput`, `WithoutPulseLine`, `ClearOutputsOnStop` | Devices | the line `wiring` gives a device; the pulse-enabled lines without it; clears held lines however the workflow stops |
| `HarpClockSet` | Devices | the first Harp time within a tolerance of UTC |
| `RequireEntries` | Devices | refuses one kind's rig.yml entries unless there are exactly `Count`, naming where the rule is set |
| `RewardForSide` | Devices | pairs a reward with its side's `RewardLine` for `PlanReward` |
| `ZapitClient`, `ZapitCommand`, `ZapitReply`, `CreateZapitQuery`, `CheckZapitConditionCount`, `ZapitConditions`, `ZapitConditionsSnapshot` | Devices (`OpenRigs.Devices.Zapit`) | the Zapit TCP protocol; the conditions file's numbering and ramp-down checks; its copy for the experiment |
| `ExposureFitsTrigger` | Video | refuses a camera exposure longer than its trigger period allows |
| `DisposeOnRenderThread` | Vision | disposes the display on the GL thread |
| `LoadRewardLine`, `PlanReward`, `FirePulses`, `HarpDelay`, `RigDevice`, `StartupWatchdog`, `ThrowError` | Core | see [Calibration](calibration.md) |

## Video logging

`DomeHardware` publishes each camera's frames on `Camera-<Name>.Data`; the workflow logs them with `OpenRigs.Logging:LogVideo.bonsai` inside its experiment logs (after `PathPrefix`), `FrameRate` from the board's `Trigger0Frequency`. It stays in the workflow because it needs the experiment folder, which an early include cannot subscribe to, and because each workflow decides which frames it records.
