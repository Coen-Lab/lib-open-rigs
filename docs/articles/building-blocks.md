# Building Blocks

Every lab workflow reads the same kind of `rig.yml`, modelled by `open_rigs.rigs.lab_rig.LabRig`, and builds on the blocks below, so each workflow keeps only its task. A block is named for what it does, never for a kind of rig: a workflow on any rig with the hardware a block opens can include it. This page lists each block: what it takes, what it gives and where it lives.

## The rig model and its parts

`open_rigs.rigs.lab_rig.LabRig` (`src/open_rigs/rigs/lab_rig.py`) is the whole of a rig's `rig.yml`. Every part is optional: a rig's file lists the parts it has, each workflow takes the ones it drives, and a name a workflow asks for and does not find stops it at Start. A workflow's own rig model is this class, and its generated C# reads the file. The parts are in `src/open_rigs/components/`:

| Part | `rig.yml` key | Module |
|---|---|---|
| Harp Behavior boards (`BehaviorBoard`, its `wiring` and `liveView`) | `behaviorBoard` | `harp_boards`, `wiring`, `live_view` |
| Timestamp generators | `timestampGeneratorGen3` | `open_rigs.devices` |
| Stepper drivers of a lick spout stage (`HarpStepperDriver`, its `wiring`) and the stage's default position | `stepperDriver`, `lickSpoutDefaultPosition` | `harp_boards`, `wiring` |
| Lick detectors | `licketySplit` | `open_rigs.devices` |
| Cameras (`SpinnakerCamera`) and the machine's `VideoEncoder` | `camera`, `videoEncoder` | `video` |
| A projector's mesh map and gamma table | `projectionCalibration` | `calibration` |
| Speakers, every one equalised, and the error sound's channel (`EqualisedSpeakerArray`) | `speakerArray` | `speakers` |
| Zapit (`ZapitServer`) | `zapit` | `zapit` |

Reward valves and the Zapit trigger are found from the Behavior board's `wiring`:

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

The model refuses a `rig.yml` whose wiring has a `RewardValve` without its `side`, two on one side, or one on a line whose output is not a `CalibratedPulse`; more than one `ZapitTrigger` on the rig, or one on a line in `outputPulseEnable` (a pulse-enabled line ends itself after its `PulseDOx` width, and the Zapit line, with the masking light on it, is held from the trigger until the workflow clears it; its `outputs` entry stays, a `FixedPulse` of width 0); and a `zapit` section with no `ZapitTrigger` line. It refuses any key it does not know, as the generated C# does at Start. Every refusal ends "(lab rig model, open_rigs.rigs.lab_rig)". A speaker array needs `errorSoundChannel` (beside `device.bootDelay`, `syncChannel` and `syncGain`) and a filter on every speaker.

## Blocks

| Block | File | Takes | Gives |
|---|---|---|---|
| `RigHardware` | `OpenRigs.Devices:RigHardware.bonsai` | subjects `RigConfigs` (the workflow's deserialised `rig.yml`), `RigConfigDirectory`, `SubjectId`; property `SpoutStepSize` | an `AsyncSubject` per device named as in the data (`BehaviorBoard-Main`, `TimestampGeneratorGen3-Main`, `StepperDriver-A`, `StepperDriver-B`, `LicketySplit-Left`, `LicketySplit-Right`, `Camera-Face`, `Camera-Pupil`, `Camera-Body`), each opened behind `<Name>.Data` (and `<Name>.Commands` for Harp devices); the cameras run on the board's trigger 0 with rig.yml's serial, exposure, gain, binning and pixel format, and stop the workflow at start if rig.yml lists other than three or an exposure does not fit the trigger period (`ExposureFitsTrigger`); `Timestamp` (the Harp clock), `HarpClockSet`, `RigFile`, `ZapitTriggerLine`, `WheelDistanceCm`, `LickStateLeft`, `LickStateRight`; takes `TriggerCalibratedPulse` and `TriggerPulse`; includes `HarpLickSpoutStage` |
| `HarpLickSpoutStage` | `OpenRigs.Devices:HarpLickSpoutStage.bonsai` | subjects `StepperDriver-A`, `StepperDriver-B`, `ControlEvents`, `LickSpoutStage.Commands`; properties `RigFile`, `PositionsFile`, `StepSize` | opens both drivers, sets them up, homes all five motors and moves the spouts in (`LickSpoutStage.Homed`); each target on `LickSpoutStage.Target`; handles the commands `SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SaveSpoutPositions`, `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftFront`, `LeftBack`, `RightFront`, `RightBack`, `BaseLeft`, `BaseRight`, which the workflow declares on `ExperimentControl` |
| `HarpLogs` | `OpenRigs.Devices:HarpLogs.bonsai` | `<Name>.Data` of each Harp device `RigHardware` opens, `PathPrefix`, `HarpClockSet` | one `LogHarpDevice` per device, `LogName` its name, started once the experiment folder exists and the Harp clock is set. Place it after `PathPrefix` is declared |
| `DeliverReward` | `OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai` | input `(side, microlitres)`; properties `RigFile`, `CalibrationDirectory`, `MissingCalibration`; subject `Timestamp` | `RewardLineLeft`, `RewardLineRight` (`LoadRewardLine`), one plan per reward on `RewardDelivered`, its pulses on `TriggerCalibratedPulse` one reward after another; a `RewardDelivered` row on `AsyncEvents` (Value the microlitres delivered, Detail starting with the side) and each calibrated line's file as `ValveCalibrationDO<n>`. Place it after `AsyncEvents` and `PathPrefix` are declared |
| `ProjectorDisplay` | `OpenRigs.Vision:ProjectorDisplay.bonsai` | input `projectionCalibration`; properties `DisplayDevice`, `WindowState`, `Width`, `Height` | the BonVision window, mesh mapping and gamma correction; stimuli draw on `DrawStimuli`; disposed on the render thread |
| `BehaviorBoard.RunningWheel` | `OpenRigs.Devices:BehaviorBoard.RunningWheel.bonsai` | the board's subjects, `CountsPerRev`, `WheelDiameterMm` | the distance run, in cm, with the encoder in Position mode |
| `Zapit.ConnectZapit` | `OpenRigs.Devices:Zapit.ConnectZapit.bonsai` | input the task's Zapit conditions file path, sent once when the experiment uses Zapit; subjects `ZapitCommands`, `ZapitReplies` (declared by the workflow), `RigConfigs`, `SubjectId` | the TCP link to `rig.yml`'s `zapit` host and port: every `ZapitCommands` value sent, every reply on `ZapitReplies`. Stops the workflow on an error reply, no conditions file loaded, a conditions count other than the file's, or a real mouse on a rig without hardware triggering |

## Operators

| Operator | Package | What it does |
|---|---|---|
| `LickSpoutStage`, `SetUpLickSpoutDriver`, `HomeLickSpoutMotor`, `MoveLickSpoutDriver`, `ToLickSpoutCommand`, `SpoutPositions` | Devices | the stage's state, setup, homing and moves; a mouse's positions file is one line, `baseTransverse,leftRadial,rightRadial,leftElevation,rightElevation` |
| `WiredOutput`, `WithoutPulseLine`, `ClearOutputsOnStop` | Devices | the line `wiring` gives a device; the pulse-enabled lines without it; clears held lines however the workflow stops |
| `HarpClockSet` | Devices | the first Harp time within a tolerance of UTC |
| `RequireEntries` | Devices | refuses one kind's rig.yml entries unless there are exactly `Count`, naming where the rule is set |
| `RewardForSide` | Devices | pairs a reward with its side's `RewardLine` for `PlanReward` |
| `ZapitClient`, `ZapitCommand`, `ZapitReply`, `CreateZapitQuery`, `CheckZapitConditionCount`, `ZapitConditions`, `ZapitConditionsSnapshot` | Devices (`OpenRigs.Devices.Zapit`) | the Zapit TCP protocol; the conditions file's numbering and ramp-down checks; its copy for the experiment |
| `ZapitTrial` (`Command`, `CheckArmed`, `CheckReply`, `RequireHardwareTrigger`, `LineMessage`, `ReplyMarginSeconds`), `CheckZapitReply`, `RequireZapitHardwareTrigger`, `ZapitLineMessage` | Devices (`OpenRigs.Devices.Zapit`) | one trial's StartStimulus; the check that Zapit's reply armed that trial's condition with the laser as asked (`CheckArmed`, the same message in every task); the stops on an error reply or no conditions file, and on a real mouse without hardware triggering; the ZapitTrigger line's set and clear; the 0.2 s a task allows for Zapit's reply |
| `LoadSpeakers`, `SpeakerBuffer`, `ErrorSoundBuffer`, `CheckWaveRate` | Audio | see [Sound](#sound) |
| `ExposureFitsTrigger` | Video | refuses a camera exposure longer than its trigger period allows |
| `DisposeOnRenderThread` | Vision | disposes the display on the GL thread |
| `QuietWait` | Core | a trial's quiet waits before Go: both spouts released for `lickQuietSeconds` (a held contact holds Go), the wheel within `wheelQuietCm`, forward and back added, over the last `wheelQuietSeconds`; one class for every task |
| `LoadRewardLine`, `PlanReward`, `FirePulses`, `HarpDelay`, `RigDevice`, `StartupWatchdog`, `ThrowError` | Core | see [Calibration](calibration.md) |

## Sound

The speakers and every sound a task plays go through `OpenRigs.Audio` ([Speakers and Sounds](../bonsai-operators/OpenRigs.Audio/speakers.md)): `LoadSpeakers` reads `rig.yml`'s `speakerArray` from `RigFile` once, checking every speaker's filter, into an `AsyncSubject` `Speakers`; `SpeakerBuffer` lays a sound (or a table of sounds) out on chosen speakers, equalised for each (a stimulus) or not (a background noise); `ErrorSoundBuffer` gives the lab's one error sound on `errorSoundChannel` alone at the task's `punishment.sound.gain`, so a task file names no error sound file. Build each buffer once at load, waiting for the task file where it sets a gain, and keep the mixer's start at least 3 s after Start (`rig.yml`'s `device.bootDelay` may lengthen it, never shorten it).

## The trial log

Every task's `schemas\trial-columns.yml` starts with the shared block, in order, with the same types, units and descriptions: the lab's 12 core columns `harpTime, trialNumber, trialType, outcome, itiStartTime, itiSeconds, trialStartTime, stimulusOnsetTime, outcomeTime, rewardSizeUl, punishSound, punishTimeoutSeconds` (`trialType`, `outcome`, `rewardSizeUl` and `punishSound` described by each task), with each opt-in block the task uses in its place: `quietWait` (`quietWaitSeconds`, after `itiSeconds`) and `opto` (`laserCondition, laserPowerMw, laserTriggerTime`, after `punishTimeoutSeconds`). The blocks are `open_rigs.rigs.trials`' `CORE_COLUMNS`, `QUIET_WAIT_COLUMNS` and `OPTO_COLUMNS`; each task's `tests\test_trial_columns.py` holds its file to them with `shared_block_problems(columns, blocks)`, naming the blocks it uses, so change a block there and regenerate every task's file. A task's own columns follow.

Keep a task's outcome logic (what counts as the response, and the reward, error sound and spouts that follow) in one C# operator that runs on every tick, the way a corridor task's state machine runs on every position update. Nodes that run only at an outcome pay .NET's first-run compile at trial 1's: in a two-spout task whose choice, row and consequence are a chain of nodes, trial 1's reward came 8 to 20 ms after its outcome and every later one within 2 ms (measured 14 Nov 2026; neither writing the row on its own thread nor compiling the task's C# at Start changed it, and only an earlier outcome through the same nodes did). `summarise-experiment` reports a reward more than 2 ms late.

`uv run summarise-experiment <experiment folder>` (`open_rigs.rigs.trials.summarise`) reads one experiment from its files alone and checks the rows against each other and the task file: trial numbers and outcomes, each Go, stimulus and laser trigger within 2 ms of where the task puts it, each interval starting at the last outcome plus its timeout, the error sound with each timeout, and a `RewardDelivered` row for each rewarded trial. A log without `quietWaitSeconds` has no quiet waits, and a task file without `optogenetics` no lead. Its performance is Correct / (Correct + Incorrect) over the trials both spouts were offered, as Rig Control shows it.

## Video logging

`RigHardware` publishes each camera's frames on `Camera-<Name>.Data`; the workflow logs them with `OpenRigs.Logging:LogVideo.bonsai` inside its experiment logs (after `PathPrefix`), `FrameRate` from the board's `Trigger0Frequency`. It stays in the workflow because it needs the experiment folder, which an early include cannot subscribe to, and because each workflow decides which frames it records.
