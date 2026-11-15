# Patched builds of other projects' packages

`local_packages\` also carries builds of packages with fixes the lab needs that are not released yet. Each has a version of its own, distinct from any upstream one, so a restore never confuses the two.

## OpenRigs.Core, Devices, Logging, Streaming, Video and Vision

Built because open-rigs has not published a release yet (ucl-open/ucl-open#12), from this repository's own `src\`: upstream branch `rig-config-resolver` at `3788bfb` (main plus the per-machine config operators `ResolveRigConfigDirectory`, `ReadRigConfig` and `ResolveRigFile`), upstream `main` merged at `f313716` (4 Nov 2026: the calibration loop, per-line pulse outputs on DO1 to DO3, camera trigger enable flags with `EnableTrigger1` off by default, and LogData CSV columns named by field, which a task's `tr-open-rigs` needs), and the lab's changes below, each a commit on this repository's `main`. Each package's version is its `.csproj`'s; claude-tools `docs/architecture/COEN-LAB.md` says how they are raised.

Until 5 Nov 2026 these were `0.1.0-bbox.N` (first built for coen-behaviour-box) and the lab's own packages `0.1.0-local.N`; from 5 to 6 Nov `0.1.1-coen.N`; every lab package is now `0.1.1-coen-lab.N`. The table names the versions as they were. On 7 Nov 2026 every lab package was raised one with no source change: `build\Common.csproj.targets` (what dependency version each nuspec names) had changed in `86daefc` with nothing flagged, and `build\` counts as each package's input since (audit-rigs set C).

| Commit | Package (version) | Change | Upstream |
|---|---|---|---|
| `8deeb9c` | Video | `TriggeredSpinnaker` records a cropped frame when the camera's stored region is smaller than the sensor: the region is reset to full frame after binning | ucl-open/ucl-open#66 |
| `ee4a8a4` | Devices (`Gain` bbox.3) | Bonsai crashes when a frame waits in the Zip, because `ChunkData` is read after Spinnaker releases the image: the `TriggeredSpinnaker` include copies the chunk fields inside the camera callback (`Gain` added in `fea83fb`, without which `LogVideo` could not build against it). The `OpenRigs.Video.TriggeredSpinnaker` operator already copies `FrameID` and `Timestamp` before the Zip | ucl-open/ucl-open#67 |
| `badd9ca` | Logging, Streaming (bbox.2) | The experiment folder is `ses-<Session>_<DateTime>`, without `date-`, as agreed on ucl-open/roadmap#37: `LogController` writes it and `StreamController`'s session key matches it. The time is still UTC | ucl-open/roadmap#37 |
| `a92cde7`, `19e39cb` | Video (bbox.3) | A new `ExposureTime` or `Gain` goes to the open camera, and `VideoChunkData` carries both per frame | Not reported |
| `4e79e7f` | Logging (bbox.4) | `LogTrial`, `LogAsync` and `CreateAsyncEvent`, the lab's two experiment files (claude-tools `COEN-LAB.md`) | Lab-only |
| `82bdb64` | Logging (bbox.5) | `CsvWriter`'s `AutoFlush` and `PublishRows` and the `LoggedRows` source, both on in `LogTrial` and `LogAsync`, so their rows are on disk and streamed as they are written. Off by default elsewhere (Pip, 5 Nov 2026); neither Bonsai's CsvWriter nor open-rigs has anything like them (Bonsai's `Buffered` moves writing to another thread, not to disk) | Not reported |
| `5b6baaa` | Logging (coen.2) | `CsvWriter.ColumnNames`, the header's columns for a type without writing, for the hold of `BonsaiData_Trial` to the lab's names (app-rig-control's names check, `names.run_check`, since 12 Nov 2026; the PowerShell script before) | Lab-only |
| `9ce8bc9` | Core, Devices (coen.2) | `ManualBalance` beside `SerialBalance`, built on `ManualWeighing`, `ManualReadings` and `ApplyBalancePort` (claude-tools `DECISIONS.md`, 6 Nov 2026) | Lab-only (Pip: "Nothing of Tom's") |
| `0bacc27`, `f2f8802`, `90558a7` | Video (coen-lab.4 to .6) | `UsbCamera`, a USB camera that is not the rig's own: exposure live in microseconds as `ExposureTime`, and a missing frame ends the stream only after none for 5 s or 4 exposures (Pip, 10 Nov 2026) | Lab-only |
| `cffa26c` | Video (coen-lab.7), Devices (coen-lab.5) | `TriggeredSpinnaker` sets `PixelFormat` each time the camera opens, Mono8 when not set, and the include exposes it: a format stored on the camera was used before (one rig's were Mono16) | Not reported (Proposed upstream, below) |
| `e137284` | Video (coen-lab.8) | A blank or null `PixelFormat` is Mono8, as coen-qc-preflight-rig reads rig.yml, not an opaque Spinnaker error | With `cffa26c` |
| `fcef3f6` | Core (.5) | The Bonsai calibration fitting stack is deleted, with Core's MathNet dependency: a curve's fit is made in Python when it is written. The pulse gap is named `pulseGapMs`. It also took MathNet.Numerics out of `.bonsai\Bonsai.config`, which Bonsai.Numerics (a dependency of BonVision and of `RandomInterval.bonsai`) still needs, so `.bonsai` could not be set up and OpenRigs.yml's image job failed until `7028e65` put it back | Lab-only |
| `423ecb5` | Core (.5) | `CalibrationCurve` carries `pulseGapMs` and its `linearFit`, which a rig plans rewards with | Lab-only |
| `1e18f65` | Core (.5) | `FirePulses` replaces `PulseBurst`: pulse trains timed on the Harp clock, each pulse `PulseGapMs` after the previous one closes | Lab-only |
| `a8c6b58` | Core (.5) | `LoadRewardLine` (one side's reward line from rig.yml's wiring and its valve calibration, read once at start) and `PlanReward` (a reward as equal pulses inside the calibrated range) | Lab-only |
| `21e53b0` | Core (.5) | `HarpDelay` (a delay on the Harp clock), `StartupWatchdog` and `ThrowError` | Lab-only |
| `1fd1e35` | Core (.5) | `RigDevice` and `NamedDevice`, a rig.yml device by kind and name | Lab-only |
| `a66a434`, `fa4c431` | Python | the first `LabRig`, the one rig model of every workflow: wiring required and checked, refusals naming the rule's home | Lab-only |
| `cce137c` | Devices (.6) | Zapit: the TCP client, its commands and replies, and the conditions file's checks | Lab-only |
| `4afd389` | Devices (.6) | `HarpLickSpoutStage`: setup, homing, moves and per-mouse positions in tested C# | Lab-only |
| `17eac88`, `0a55cab` | Devices (.6) | `DeliverReward`: each (side, microlitres) reward planned and fired as calibrated pulses on the Harp clock, logged as water delivered | Lab-only |
| `0a55cab`, `888a82b` | Devices (.6) | `RigHardware` (the rig's boards, stage and three cameras) and `HarpLogs` | Lab-only |
| `0ca2c36` | Devices (.6) | `BehaviorBoard.RunningWheel` reads the encoder in Position mode, so it gives distance | Lab-only |
| `0aea718`, `cd12107`, `ad76b13` | Vision (.1) | `ProjectorDisplay` and `DisposeOnRenderThread`; Vision is built as a lab package for the first time | Lab-only |
| `5891c48` | Video (.9) | `ExposureFitsTrigger`, with `OpenRigs.Video.Tests` | Lab-only |
| `62f827c` | Core (.6) | `FirePulses` and `HarpDelay` refuse a clock that steps back only while pulses or delays are pending; idle, they take the new time (SynchronizeTimestamp sets the board's clock at start) | Lab-only |
| `5a76011` | Core (.6), Devices (.7) | `DeliverReward` fires every reward through one `FirePulses`, so a queued reward starts `pulseGapMs` after the last; `LoadRewardLine`'s `MaxRewardUl` (20 by default) and `PlanReward` refuse a larger reward or one over 65535 pulses | Lab-only |
| `4dfe52c` | Devices (.7) | `ZapitTrial.Command` and `ZapitLineMessage`, the per-trial Zapit command (0 mW on a laser-off trial) and trigger line write the rigs share | Lab-only |
| `9fb7af5` | Core (.6) | `DispenseProcedure.requireMonotone` deleted, as nothing read it | Lab-only |
| `5665dd7` | Python | `LabRig` refuses keys a rig does not use, as the generated C# does at Start; wiring refusals give the line to add | Lab-only |
| `55afb94` | Devices (.8), Python | `Zapit.ConnectZapit`, the rigs' Zapit connection and its start checks (`CheckZapitReply`, `CheckZapitConditionCount`, `RequireZapitHardwareTrigger`) once; `ZapitTrial.ReplyMarginSeconds` and `ZAPIT_REPLY_MARGIN_SECONDS`; every speaker needs a filter (`RequireFilters`) | Lab-only |
| `7cc06df` | Python | `open_rigs.rigs.lab_rig`'s `MAX_REWARD_UL` (20) and `ZAPIT_MAX_RAMP_DOWN_SECONDS` (0.5), held by C# tests to `LoadRewardLine.DefaultMaxRewardUl`, `DeliverReward`'s two `MaxRewardUl` and `ZapitConditions.MaximumRampDownMs`; no package raised | Lab-only |
| `954353d` | Python | `LabRig` refuses a board whose ZapitTrigger line is in `outputPulseEnable`: the line, and the masking light on it, is held from the trigger until the workflow clears it, never a board-timed pulse (14 Nov 2026). `RigHardware`'s `WithoutPulseLine` already dropped it at run time; no package raised | Lab-only |
| `7028e65` | None | The lab's packages Control, HarpDevices, LiveView and Manifest are in `OpenRigs.sln` (not their `.Tests` console programs, which `records.yml` runs), so OpenRigs.yml's restore covers them and its documentation job builds them; MathNet.Numerics 4.15.0 is back in `.bonsai\Bonsai.config` (`fcef3f6`); no package raised | Lab-only |
| `1aeae37` | Vision (.2) | `DisposeOnRenderThread` writes the shader window source's error to the console, where it was dropped | Lab-only |
| `3203626` | Core (.7) | `HarpDelay` sends outside its lock, one thread at a time: sending under it deadlocked a rig when an input from another thread ended the delay (14 Nov 2026) | Lab-only |
| `5ead3b3` | Logging (.7) | `CsvWriter` names a Harp-timestamped record's first column `harpTime`, not `Seconds`, so a file says which clock its times are on, and `specs/logging.md` section 4 names it (14 Nov 2026). `LogVideo` and `LogRawVideo` are upstream's again (15 Nov 2026: no workflow uses them), so their CSVs still start `Seconds` | Lab-only |
| `54e3f83` | Core (.8) | `QuietWait`, the tasks' quiet waits before Go, one class for every task | Lab-only |
| `72e4d1e` | Python | `summarise-experiment` checks each Go and the laser trigger against the task file, the outcomes' values and the error sound against the timeout; its performance is Correct / (Correct + Incorrect) over two-spout trials; the tasks' shared block gains `laserTriggerTime` (16 columns) | Lab-only |
| `c621a3b` | Devices (.9) | `ZapitTrial.CheckArmed`, the tasks' one check of Zapit's armed reply | Lab-only |
| `842fc31` | Audio (.1), Devices (.9) | New package `OpenRigs.Audio`: `SpeakerSet` (a rig.yml's speakers and filters, checked; `RequireFilters` moved here from Devices), `SpeakerBuffer`, `ErrorSoundBuffer` (one error sound, embedded, for every task) and `CheckWaveRate` | Lab-only |
| `6b1a0ce` | Video (.11), Logging (.8), Python | `FfmpegVideoWriter` and the include `LogVideoFfmpeg`: each camera to a kill-safe .mkv through ffmpeg on the rig's encoder (`videoEncoder` in rig.yml: h264_nvenc, h264_qsv or libx264), a bounded queue that never blocks the camera, every frame written at Stop (at most 20 s), and a CSV of harpTime, frameId, cameraClockNs, exposureUs and gainDb; `check-videos` (and `summarise-experiment --videos`) counts each video's decoded frames against its CSV | Lab-only |
| `63f5a39` | Devices (.10), Vision (.3), Python | The lab's blocks named for what they do: `RigHardware`, `HarpLogs` and `ProjectorDisplay`; one `open_rigs.rigs.lab_rig.LabRig`, every part optional, for every workflow, its parts in `open_rigs.components`; `open_rigs.rigs.trials`, its shared block a lab core plus the opt-in `opto` and `quietWait` blocks | Lab-only |
| `b60e9e5` | Core (.9), Devices (.12), Logging (.11), Python | `QuietWait`'s constructor takes `lickQuietSeconds`, `wheelQuietSeconds` and `wheelQuietCm`, the stage settings' names; `ZapitTrial.CheckArmed` takes `laserCondition`; `BinaryLogWriter.Path` is `FileName`, as every other writer's; `LoadRewardLine` and `DeliverReward` have no `MissingCalibration`, so a reward line without a calibration always stops the workflow (no `DeliverNothing`, 15 Nov 2026); `open_rigs.core.task` `QuietWait` and `Optogenetics`, the blocks every task's model takes | Lab-only |
| `818e890` | Audio (.3), Manifest (.18), Python | The sound and pause after a wrong or missed trial are the penalty: `PenaltySound`, `PenaltySoundBuffer` and `penalty_tone.wav` on rig.yml's `penaltySoundChannel`; the shared trial columns `penaltySound` and `penaltyTimeoutSeconds`; `open_rigs.core.task` `Penalty`, the task block both two-spout tasks take, its sound on by default (15 Nov 2026); trial columns carry no earlier names | Lab-only |
| `677556b` | Audio (.5), Devices (.14), Python | One shared speaker mixer, `SpeakerMixer.bonsai` (the speakers, the mixer no sooner than 3 s after Start, and its four sound subjects), and every sound at a level in dBFS RMS: `SoundLevel` (the lab's defaults, the -1 dBFS peak check after equalisation), `LevelDbfs` on `SpeakerBuffer` and `PenaltySoundBuffer` (no `Gain`), `SpeakerSet.Buffer` at a level and `Layout` at a linear gain; `open_rigs.core.task` `StimulusSound`, `RewardTone`, `BackgroundNoise` and `PenaltySound.levelDbfs`. `RigHardware` starts the cameras' trigger and reports Ready behind a 180 s `StartupWatchdog` | Lab-only |
| `c75dd66`, `fc64ffc` | Core (.13), Devices (.15), Python | `ReadLabRig.bonsai`, this machine's `rig.yml` read once into the lab rig model `LabRig`, whatever the rig (`RigConfigDirectory`, `RigConfigs`); `WiredOutputs`, every line `wiring` gives a device, such as the `RewardValve`s; `GapWatchdog`, a stream that stops arriving mid-run stops the workflow with its own message (its deadline on a thread of its own); one name for a valve pulse width: `DispenseSample.pulseWidthMs` and `DispenseProcedure.primingPulseWidthMs` (were `size`, `primingSize`) | Lab-only |
| `08a9881`, `0d645e4` | Core (.14), Audio (.7), Python | `SpeakerMixer` plays `StimulusSounds`, `RewardToneSounds` and `PenaltySounds` through a `PublishSubject` each, so a sound sent while the mixer boots is not played late at its start; `DispenseProcedure` has no `xUnit` (`pulseWidthMs` is in ms) | Lab-only |
| `76aeb53`, `7b685e2` | Core (.15), Audio (.8), Python | `SpeakerMixer` runs `StimulusSounds` and `PenaltySounds` through `CheckMixerStarted`, so one sent while the mixer boots stops the workflow, naming it; `PlanReward` stops on a reward that is not a number or infinite, naming the side | Lab-only |
| `329441c`, `d27c3c9` | Core (.16), Devices (.16), Python | One name for a valve pulse's width and count: `PulseWidthMs` and `Pulses` on `FirePulses`, `RewardPlan` and `DeliverReward` (its RewardDelivered detail `pulseWidthMs=`, `pulseGapMs=`; were `WidthMs`, `Count`, `width=`, `gap=`); `vision.ProjectionCorrection` deleted, as nothing used it | Lab-only |
| `7572616` | Logging (.9) | `BinaryLogWriter` writes `LogHarp` and `LogHarpDemux`'s `.bin` files in place of `MatrixWriter`, on a foreground thread that keeps headless Bonsai open until each file is closed: `MatrixWriter`'s background thread died when Bonsai exited, so a log could lose its last messages or never be created (`LogHarpTests` failed in 9 of 20 runs under parallel load, none of 20 with it) | Not reported |
| `87dd494` | Logging (.12), Devices (.13) | `LogCameras` (each camera `RigHardware` opens, logged by `LogVideoFfmpeg`) and `Zapit.ZapitLogs` | Lab-only |
| `1f4c719`, `d5b3ea7` | Core (.17), Devices (.17), Audio (.9), Python | `RigHardware` reports Ready once every stage its `ReadyWaitsFor` names has come (`HarpClockSet, SpoutsHomed` by default), and `SpeakerMixer` multicasts `MixerStarted` into `RigHardware`'s `ReadyStages` once the mixer has started, so a workflow with speakers sets `ReadyWaitsFor` to `HarpClockSet, SpoutsHomed, MixerStarted` and Go waits for the mixer; `StartupWatchdog` emits its last stage when complete; rig.yml's `speakerArray.device.bootDelaySeconds` and SerialBalance's `bootDelaySeconds` (were `bootDelay`, `bootDelayS`); no per-speaker `gain`; `DispenseSample.pulses` (was `repeats`); `workflow_lint.fixed_width_pulses_on_reward_valves` | Lab-only |

OpenRigs.yml's publish job also pushes each green build's packages, versioned `0.0.0-ciN`, to Coen-Lab's GitHub Packages feed. No workflow installs from that feed: workflows install the `-coen-lab.N` builds in this repository's `local_packages\`, which app-rig-setup's `workflows\update-workflows.ps1` copies into each one.

`OpenRigs.Streaming` is otherwise upstream's `src/OpenRigs.Streaming` at the base commit. It declares Bonsai.Dsp and Bonsai.Vision 2.9.0 and Bonsai.Scripting.Expressions 2.10.0; Bonsai restores only what a workflow's `Bonsai.config` lists, at the listed versions, so a workflow on 2.8.0 runs it on its 2.8.0 assemblies.

## Switching to upstream

When open-rigs publishes a release in which every row of the table above is upstream, with the config operators, delete the patched packages here, and in each workflow set the released version in `.bonsai\Bonsai.config` and remove its copy from `local_packages\`. app-rig-setup's `workflows\update-workflows.ps1` then leaves those packages alone, since it only handles packages in this repository's `local_packages\`.

## Proposed upstream

- `TriggeredSpinnaker` `PixelFormat` (cffa26c, Video 0.1.1-coen-lab.7 and Devices .5; blank means Mono8 since Video .8, audit 11 Nov 2026): upstream's operator never sets the format, so a camera opens in whatever format it stored (one rig's were Mono16), and LogVideo writes 8-bit frames only. To offer to ucl-open as an issue with this diff (the table above lists it), made from the code against upstream `main` at `f313716` (regenerated 14 Nov 2026); not yet reported.

```diff
--- a/src/OpenRigs.Video/TriggeredSpinnaker.cs
+++ b/src/OpenRigs.Video/TriggeredSpinnaker.cs
@@ -15,6 +15,7 @@
         {
             ExposureTime = 1e6 / 50 - 1000;
             Binning = 1;
+            PixelFormat = PixelFormatEnums.Mono8.ToString();
         }
 
         [Description("The duration of each individual exposure, in microseconds. In general, this should be 1 / frameRate - 1 millisecond to prepare for next trigger.")]
@@ -26,10 +27,18 @@
         [Description("The size of the binning area of the sensor, e.g. a binning size of 2 specifies a 2x2 binning region.")]
         public int Binning { get; set; }
 
+        // A string, not PixelFormatEnums, so rig.yml's pixelFormat maps onto it; the camera refuses an
+        // unknown name when it opens.
+        [Description("The pixel format the camera sends, a Spinnaker PixelFormat name LogVideo can write, Mono8 (rig.yml's pixelFormat; blank is Mono8). Set every time the camera opens, so a format stored on the camera is never used. Taken only when the camera opens.")]
+        public string PixelFormat { get; set; }
+
         protected override void Configure(IManagedCamera camera)
         {
             try { camera.AcquisitionStop.Execute(); }
             catch { }
+            // Before binning and the region: a format change can change their limits. Blank means Mono8,
+            // as coen-qc-preflight-rig reads it (audit, 11 Nov 2026).
+            camera.PixelFormat.Value = string.IsNullOrWhiteSpace(PixelFormat) ? "Mono8" : PixelFormat.Trim();
             camera.BinningSelector.Value = BinningSelectorEnums.All.ToString();
             camera.BinningHorizontalMode.Value = BinningHorizontalModeEnums.Sum.ToString();
             camera.BinningVerticalMode.Value = BinningVerticalModeEnums.Sum.ToString();
--- a/src/OpenRigs.Devices/TriggeredSpinnaker.bonsai
+++ b/src/OpenRigs.Devices/TriggeredSpinnaker.bonsai
@@ -14,6 +14,7 @@
         <Property Name="Binning" />
         <Property Name="SerialNumber" />
         <Property Name="ColorProcessing" />
+        <Property Name="PixelFormat" />
       </Expression>
       <Expression xsi:type="Combinator">
         <Combinator xsi:type="p1:TriggeredSpinnaker">
@@ -22,6 +23,7 @@
           <p1:ExposureTime>19000</p1:ExposureTime>
           <p1:Gain>0</p1:Gain>
           <p1:Binning>1</p1:Binning>
+          <p1:PixelFormat>Mono8</p1:PixelFormat>
         </Combinator>
       </Expression>
       <Expression xsi:type="ExternalizedMapping">
```
