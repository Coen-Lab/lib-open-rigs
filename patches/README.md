# What the lab packages carry over upstream

`local_packages\` holds builds of packages with changes not released upstream, each versioned `0.1.1-coen-lab.N` (its `.csproj`), so a restore never confuses them with an upstream release. They are built from this repository's `src\`: open-rigs' branch `rig-config-resolver` at `3788bfb` (the per-machine config operators `ResolveRigConfigDirectory`, `ReadRigConfig`, `ResolveRigFile`), upstream `main` merged at `f313716` (4 Nov 2026), and the changes below. open-rigs has published no release yet (ucl-open/ucl-open#12). `build\` counts as every package's input.

Upstream column: an issue where reported, else "Not reported" for a fix upstream would want, "Own" for an addition of this repository's.

| Package | Carries | Upstream |
|---|---|---|
| Audio | `SpeakerSet`, `SpeakerBuffer` (levels in dBFS RMS, `SyncCopy`), `SoundLevel`, `CheckWaveRate`; `SpeakerMixer` (the mixer no sooner than 3 s after Start, `MixerStarted` into `ReadyStages`, its sound subjects through `CheckMixerStarted`); `PenaltySound`, `PenaltySoundBuffer`, `PenaltySoundSeconds`, `PlayPenaltySound`, `BackgroundNoise` | Own |
| Control | `ControlServer` (Ready, Go and Stop from a controller; parameters and commands), `ReportPhase`, `DeclareControls` (pieces declare their own controls; a name declared twice stops at Start), `DeclareDeviceControls`, `OnDeviceParameterSet`, `DeviceParameterDefault`, `DeviceParameterMaximum` (a device's controls named `<device>.<name>`), `EndAfterStop`, `ExperimentControl`, `LaunchSettings` | Own |
| Core | `RigDevice`, `NamedDevice`, `RigConfigDirectory` (`ResolveRigFile` refuses a full path, naming its `Setting`); `HarpDelay`, `FirePulses`, `LoadRewardLine`, `PlanReward` (rewards as calibrated pulses on the Harp clock); `CalibrationCurve` with `pulseGapMs` and `linearFit` (fits in Python, no MathNet); `ManualBalance`, `WeighStable`, `PollBalance`; `StartupWatchdog`, `GapWatchdog`, `OnNextTick`, `AnnounceStage`, `RequireValue`, `ThrowError`; `QuietWait`; `TaskBlocks` (the Iti, ResponseWindow and AutoReward blocks' checks) | Own |
| Devices | Device pieces, one include per device instance (`BehaviorBoard`, `PulseOutputs`, `ZapitTriggerLine`, `RunningWheel`, `LicketySplit`, `LickSpoutStage`, `SpinnakerCamera`, `UsbCamera`, `CameraLiveControls`, `RewardValve`), the core (`ReadLabRig`, `ReadyGate`, `ClockFromTimestampGenerator`, `ClockFromBoard`, `CameraTriggers`), the rig lists `DomeHardware` and `BBoxHardware` and their taps `DomeHardwareTaps` and `BBoxHardwareTaps`, `PreflightChecks`, the balances `SerialBalance` and `ManualBalance`; Zapit (`ConnectZapit`, `ConnectForTask`, `ZapitTrial`, `ZapitConditions`); `Rewards`, `ManualPulses`, `SetZapitTriggerLine`; `LabRig`, the generated rig model | Own |
| Trials | The trial loop on OpenRigs.Devices: `TrialLoop`, `ITrialRules`, `ITrial`, `ITrialOutcome`, `TrialTiming`, `LaserTiming`, `TrialCue`, `TrialTimes`, `TrialEvent`, `PickTrialEvents`, `SharedTrialColumns`, `TrialOutcome<TRow>`, `TrialOutcomes`, `ZapitTrialCommand` and `IZapitTrial`; `Penalties`, `PenaltyFlash`, `PenaltyFlashRules` and `CheckPenaltyFlashes` | Own |
| HarpDevices | `RecordHarpDevices`, every rig.yml Harp device logged with its `_device.json` and register map | Own |
| LiveView | `StreamDevices`, each published `<Kind>-<Instance>.Data` streamed on fixed ports, with its own `liveview` report | Own |
| Logging | Experiment folder `ses-<Session>_<DateTime>` (no `date-`; local time and UTC offset) in `LogController` | ucl-open/roadmap#37 |
| Logging | `CsvWriter`'s `AutoFlush`, `PublishRows`, `LoggedRows`, `ColumnNames`, and a `harpTime` first column for a Harp-timestamped record | Not reported |
| Logging | `BinaryLogWriter` for `LogHarp` and `LogHarpDemux` in place of `MatrixWriter`, on a foreground thread so headless Bonsai stays open until each file is closed | Not reported |
| Logging | `LogTrials`, `LogEvents`, `CreateEvent`, `ExperimentLogs`, `LogControlEvents`, `LogRngSeed`, `TaskLogs` (no `WorkflowOutput`), `LogVideoFfmpeg`, `ExperimentRecords`, `NoExperimentFolder` | Own |
| Manifest | `ExperimentManifest` (`manifest.json` version 4, `ExpectedFiles`, `{RigFiles}`, the trial columns from `schemas\trial-columns.yml`) | Own |
| Streaming | Stream controllers on the task pool, so Stop cannot hang; otherwise upstream's at the base commit | Not reported |
| Video | `TriggeredSpinnaker`: the region reset to full frame after binning; the chunk fields copied inside the camera callback; a new `ExposureTime` or `Gain` sent to the open camera, both per frame in `VideoChunkData`; `Name`, `CamerasOpen` once streaming, frame gaps filled | ucl-open/ucl-open#66, #67 |
| Video | `FfmpegVideoWriter` (a kill-safe .mkv on rig.yml's `videoEncoder`, every frame written at Stop, a frame CSV); `UsbCamera` | Own |
| Vision | `ProjectorDisplay` (its window and corrections in one group), `RawProjectorDisplay` and its `ProjectorWindow`, `DisposeOnRenderThread` (a stuck dispose falls back; the shader window's error written out), `RequireDisplay`, `PlayPenaltyFlash` | Own |
| Python | `open_rigs.rigs.lab_rig.LabRig` and its parts in `open_rigs.components`, generated into Devices and Core; `workflow_lint`; `summarise-experiment`, `check-videos`; the task blocks in `open_rigs.core.task`; `MAX_REWARD_UL` (there, held to `LoadRewardLine.DefaultMaxRewardUl` by a Python test) and `ZAPIT_MAX_RAMP_DOWN_SECONDS` (held to its C# twin by a C# test) | Own |

`OpenRigs.Streaming` declares Bonsai.Dsp and Bonsai.Vision 2.9.0 and Bonsai.Scripting.Expressions 2.10.0; Bonsai restores only what a workflow's `Bonsai.config` lists, so a workflow on 2.8.0 runs it on its 2.8.0 assemblies.

The lab's pins differ from upstream's: every package and `.bonsai\Bonsai.config` on YamlDotNet 17.1.0 (upstream's Core on 16.0.0, open-rigs/open-rigs#41), and `.bonsai\Bonsai.config` on the workflows' Bonsai.Scripting.Expressions 2.10.0, System.Buffers 4.6.1, System.Memory 4.6.3, System.Numerics.Vectors 4.6.1 and System.Runtime.CompilerServices.Unsafe 6.1.2 (22 Nov 2026), so the lab's workflow update finds one third-party pin set across this file and every workflow.

OpenRigs.yml's publish job also pushes each green build's packages, versioned `0.0.0-ciN`, to the organisation's GitHub Packages feed. No workflow installs from that feed.

## Switching to upstream

When open-rigs releases every "Not reported" and issue row above, with the config operators, delete those packages here, set the released version in each workflow's `.bonsai\Bonsai.config` and remove its copy from `local_packages\`.

## Proposed upstream

`TriggeredSpinnaker` `PixelFormat`: withdrawn; the lab adopted upstream's meaning (blank keeps the camera's stored format). Upstream's branch `triggered-spinnaker` carries the format (`518ca24`), the chunk copy inside the callback (`7a441c2`, ucl-open#67), the region reset after binning (`1d0b4a2`, ucl-open#66) and a start signal for the cameras (`6f81c6c`). Converging the two is open.
