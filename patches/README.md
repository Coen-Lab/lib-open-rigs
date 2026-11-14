# Patched builds of other projects' packages

`local_packages\` also carries builds of packages with fixes the lab needs that are not released yet. Each has a version of its own, distinct from any upstream one, so a restore never confuses the two.

## OpenRigs.Core, Devices, Logging, Streaming, Video and Vision

Built because open-rigs has not published a release yet (ucl-open/ucl-open#12), from this repository's own `src\`: upstream branch `rig-config-resolver` at `3788bfb` (main plus the per-machine config operators `ResolveRigConfigDirectory`, `ReadRigConfig` and `ResolveRigFile`), upstream `main` merged at `f313716` (4 Nov 2026: the calibration loop, per-line pulse outputs on DO1 to DO3, camera trigger enable flags with `EnableTrigger1` off by default, and LogData CSV columns named by field, which av-dome's `tr-open-rigs` needs), and the lab's changes below, each a commit on this repository's `main`. Each package's version is its `.csproj`'s; claude-tools `docs/architecture/COEN-LAB.md` says how they are raised.

Until 5 Nov 2026 these were `0.1.0-bbox.N` (first built for coen-behaviour-box) and the lab's own packages `0.1.0-local.N`; from 5 to 6 Nov `0.1.1-coen.N`; every lab package is now `0.1.1-coen-lab.N`. The table names the versions as they were. On 7 Nov 2026 every lab package was raised one with no source change: `build\Common.csproj.targets` (what dependency version each nuspec names) had changed in `86daefc` with nothing flagged, and `build\` counts as each package's input since (audit-rigs set C).

| Commit | Package (version) | Change | Upstream |
|---|---|---|---|
| `8deeb9c` | Video | `TriggeredSpinnaker` records a cropped frame when the camera's stored region is smaller than the sensor: the region is reset to full frame after binning | ucl-open/ucl-open#66 |
| `ee4a8a4` | Devices (`Gain` bbox.3) | Bonsai crashes when a frame waits in the Zip, because `ChunkData` is read after Spinnaker releases the image: the `TriggeredSpinnaker` include copies the chunk fields inside the camera callback (`Gain` added in `fea83fb`, without which `LogVideo` could not build against it). The `OpenRigs.Video.TriggeredSpinnaker` operator already copies `FrameID` and `Timestamp` before the Zip | ucl-open/ucl-open#67 |
| `badd9ca` | Logging, Streaming (bbox.2) | The experiment folder is `ses-<Session>_<DateTime>`, without `date-`, as agreed on ucl-open/roadmap#37: `LogController` writes it and `StreamController`'s session key matches it. The time is still UTC | ucl-open/roadmap#37 |
| `a92cde7`, `19e39cb` | Video (bbox.3) | A new `ExposureTime` or `Gain` goes to the open camera, and `VideoChunkData` carries both per frame | Not reported |
| `4e79e7f` | Logging (bbox.4) | `LogTrial`, `LogAsync` and `CreateAsyncEvent`, the lab's two experiment files (claude-tools `COEN-LAB.md`) | Lab-only |
| `82bdb64` | Logging (bbox.5) | `CsvWriter`'s `AutoFlush` and `PublishRows` and the `LoggedRows` source, both on in `LogTrial` and `LogAsync`, so their rows are on disk and streamed as they are written. Off by default elsewhere (Pip, 5 Nov 2026); neither Bonsai's CsvWriter nor open-rigs has anything like them (Bonsai's `Buffered` moves writing to another thread, not to disk) | Not reported |
| `fea83fb` | Logging (bbox.6) | Upstream's `LogVideo` left the chunk start out of its two inline `FormatFileName` copies, so files were `<LogName>\<LogName>.avi` and an experiment crossing the hour opened the same file twice. It includes `OpenRigs.Logging:FormatFileName.bonsai` as `LogRawVideo` does, giving `<LogName>\<LogName>_<DateTime>.avi` and `.csv`; a test holds it equal to `LogRawVideo` apart from its frame selector and CSV columns | Not reported |
| `5b6baaa` | Logging (coen.2) | `CsvWriter.ColumnNames`, the header's columns for a type without writing, for the hold of `BonsaiData_Trial` to the lab's names (app-rig-control's names check, `names.run_check`, since 12 Nov 2026; the PowerShell script before) | Lab-only |
| `9ce8bc9` | Core, Devices (coen.2) | `ManualBalance` beside `SerialBalance`, built on `ManualWeighing`, `ManualReadings` and `ApplyBalancePort` (claude-tools `DECISIONS.md`, 6 Nov 2026) | Lab-only (Pip: "Nothing of Tom's") |
| `f309da2` | Logging (coen-lab.6) | `LogVideo`'s CSV drops `ExposureTime` and `Gain`, leaving `Seconds`, `FrameID` and `Timestamp`: both are fixed while recording and recorded once in the experiment's settings (Pip, 10 Nov 2026) | Lab-only |
| `0bacc27`, `f2f8802`, `90558a7` | Video (coen-lab.4 to .6) | `UsbCamera`, a USB camera that is not the rig's own: exposure live in microseconds as `ExposureTime`, and a missing frame ends the stream only after none for 5 s or 4 exposures (Pip, 10 Nov 2026) | Lab-only |
| `cffa26c` | Video (coen-lab.7), Devices (coen-lab.5) | `TriggeredSpinnaker` sets `PixelFormat` each time the camera opens, Mono8 when not set, and the include exposes it: a format stored on the camera was used before (BLUE-OPTO-1's were Mono16) | Not reported (Proposed upstream, below) |
| `e137284` | Video (coen-lab.8) | A blank or null `PixelFormat` is Mono8, as coen-qc-preflight-rig reads rig.yml, not an opaque Spinnaker error | With `cffa26c` |
| `fcef3f6` | Core (.5) | The Bonsai calibration fitting stack is deleted, with its MathNet dependency: a curve's fit is made in Python when it is written. The pulse gap is named `pulseGapMs` | Lab-only |
| `423ecb5` | Core (.5) | `CalibrationCurve` carries `pulseGapMs` and its `linearFit`, which a rig plans rewards with | Lab-only |
| `1e18f65` | Core (.5) | `FirePulses` replaces `PulseBurst`: pulse trains timed on the Harp clock, each pulse `PulseGapMs` after the previous one closes | Lab-only |
| `a8c6b58` | Core (.5) | `LoadRewardLine` (one side's reward line from rig.yml's wiring and its valve calibration, read once at start) and `PlanReward` (a reward as equal pulses inside the calibrated range) | Lab-only |
| `21e53b0` | Core (.5) | `HarpDelay` (a delay on the Harp clock), `StartupWatchdog` and `ThrowError` | Lab-only |
| `1fd1e35` | Core (.5) | `RigDevice` and `NamedDevice`, a rig.yml device by kind and name | Lab-only |
| `a66a434`, `fa4c431` | Python | `open_rigs.rigs.dome` `DomeRig`, the one rig model of every dome workflow: wiring required and checked, refusals naming the rule's home | Lab-only |
| `cce137c` | Devices (.6) | Zapit: the TCP client, its commands and replies, and the conditions file's checks | Lab-only |
| `4afd389` | Devices (.6) | `HarpLickSpoutStage`: setup, homing, moves and per-mouse positions in tested C# | Lab-only |
| `17eac88`, `0a55cab` | Devices (.6) | `DeliverReward`: each (side, microlitres) reward planned and fired as calibrated pulses on the Harp clock, logged as water delivered | Lab-only |
| `0a55cab`, `888a82b` | Devices (.6) | `DomeHardware` (the dome's boards, stage and three cameras) and `DomeHarpLogs` | Lab-only |
| `0ca2c36` | Devices (.6) | `BehaviorBoard.RunningWheel` reads the encoder in Position mode, so it gives distance | Lab-only |
| `0aea718`, `cd12107`, `ad76b13` | Vision (.1) | `DomeDisplay` and `DisposeOnRenderThread`; Vision is built as a lab package for the first time | Lab-only |
| `5891c48` | Video (.9) | `ExposureFitsTrigger`, with `OpenRigs.Video.Tests` | Lab-only |
| `62f827c` | Core (.6) | `FirePulses` and `HarpDelay` refuse a clock that steps back only while pulses or delays are pending; idle, they take the new time (SynchronizeTimestamp sets the board's clock at start) | Lab-only |
| `5a76011` | Core (.6), Devices (.7) | `DeliverReward` fires every reward through one `FirePulses`, so a queued reward starts `pulseGapMs` after the last; `LoadRewardLine`'s `MaxRewardUl` (20 by default) and `PlanReward` refuse a larger reward or one over 65535 pulses | Lab-only |
| `4dfe52c` | Devices (.7) | `ZapitTrial.Command` and `ZapitLineMessage`, the per-trial Zapit command (0 mW on a laser-off trial) and trigger line write the domes share | Lab-only |
| `9fb7af5` | Core (.6) | `DispenseProcedure.requireMonotone` deleted, as nothing read it | Lab-only |
| `5665dd7` | Python | `DomeRig` refuses keys a dome does not use, as the generated C# does at Start; wiring refusals give the line to add | Lab-only |
| `55afb94` | Devices (.8), Python | `Zapit.ConnectZapit`, the domes' Zapit connection and its start checks (`CheckZapitReply`, `CheckZapitConditionCount`, `RequireZapitHardwareTrigger`) once; `ZapitTrial.ReplyMarginSeconds` and `ZAPIT_REPLY_MARGIN_SECONDS`; every dome speaker needs a filter (`DomeSpeakers.RequireFilters`, `DomeSpeakerArray`) | Lab-only |
| `7cc06df` | Python | `open_rigs.rigs.dome`'s `MAX_REWARD_UL` (20) and `ZAPIT_MAX_RAMP_DOWN_SECONDS` (0.5), held by C# tests to `LoadRewardLine.DefaultMaxRewardUl`, `DeliverReward`'s two `MaxRewardUl` and `ZapitConditions.MaximumRampDownMs`; no package raised | Lab-only |
| `954353d` | Python | `DomeRig` refuses a board whose ZapitTrigger line is in `outputPulseEnable`: the line, and the masking light on it, is held from the trigger until the workflow clears it, never a board-timed pulse (14 Nov 2026). `DomeHardware`'s `WithoutPulseLine` already dropped it at run time; no package raised | Lab-only |

`OpenRigs.Streaming` is otherwise upstream's `src/OpenRigs.Streaming` at the base commit. It declares Bonsai.Dsp and Bonsai.Vision 2.9.0 and Bonsai.Scripting.Expressions 2.10.0; Bonsai restores only what a workflow's `Bonsai.config` lists, at the listed versions, so a workflow on 2.8.0 runs it on its 2.8.0 assemblies.

## Bonsai.FFmpeg 0.2.0-uniquepipe

[bonsai-rx/ffmpeg](https://github.com/bonsai-rx/ffmpeg) tag `v0.2.0` (`ee403aeca3d6ecb214487a5c41d94b2f2b541526`) plus `bonsai-ffmpeg-unique-pipe.patch`, which names each recording's pipe after the file stem plus a GUID, so a Start while the previous recording's ffmpeg is still draining no longer fails with "All pipe instances are busy." The DLL was built with `dotnet build -c Release` and put into the nuget.org `Bonsai.FFmpeg` 0.2.0 package (signature removed), first in Coen-Lab/hw-behaviour-box. DLL SHA-256 `ecb50c0d0618e5e31969d9c7a45a1b82a7f362db97a5cc831e884087433a5403`; package SHA-256 `43e124e7608ff0ec32ced7edf5b5fd2ff34c4072d027ccd75c1d76fa1e950d00`.

The lab's workflows record video with it rather than Bonsai.Vision's `VideoWriter` (what `OpenRigs.Logging:LogVideo` uses), because that writer encodes FMP4 on one thread inside Bonsai: on coen-behaviour-box's Intel N150 at 1440 x 1080, 50 Hz it fell behind whenever the CPU was busy, queued about 1 GB of frames a minute in RAM, and crashed a 30-minute experiment at about 23 minutes (hw-behaviour-box 25a0bcd). FFmpeg encodes H.264 on Intel Quick Sync with flat memory.

## Switching to upstream

When open-rigs publishes a release in which every row of the table above is upstream, with the config operators, or Bonsai.FFmpeg one with a unique pipe name, delete the patched package here, and in each workflow that uses it set the released version in `.bonsai\Bonsai.config` and remove its copy from `local_packages\`. app-rig-setup's `workflows\update-workflows.ps1` then leaves that package alone, since it only handles packages in this repository's `local_packages\`. Delete `bonsai-ffmpeg-unique-pipe.patch` with the FFmpeg package.

## Proposed upstream

- `TriggeredSpinnaker` `PixelFormat` (cffa26c, Video 0.1.1-coen-lab.7 and Devices .5; blank means Mono8 since Video .8, audit 11 Nov 2026): upstream's operator never sets the format, so a camera opens in whatever format it stored (BLUE-OPTO-1's were Mono16), and LogVideo writes 8-bit frames only. To offer to ucl-open as an issue with this diff (the table above lists it); not yet reported.

```diff
--- a/src/OpenRigs.Video/TriggeredSpinnaker.cs
+++ b/src/OpenRigs.Video/TriggeredSpinnaker.cs
@@ -15,6 +15,7 @@ namespace OpenRigs.Video
         {
             ExposureTime = 1e6 / 50 - 1000;
             Binning = 1;
+            PixelFormat = PixelFormatEnums.Mono8.ToString();
         }
@@ -62,6 +63,11 @@ namespace OpenRigs.Video
         public int Binning { get; set; }
 
+        // A string, not PixelFormatEnums, so rig.yml's pixelFormat maps onto it; the camera refuses an
+        // unknown name when it opens.
+        [Description("The pixel format the camera sends, a Spinnaker PixelFormat name such as Mono8 or Mono16 (rig.yml's pixelFormat). Set every time the camera opens, so a format stored on the camera is never used. Taken only when the camera opens.")]
+        public string PixelFormat { get; set; }
+
@@ -81,6 +87,8 @@ namespace OpenRigs.Video
             try { camera.AcquisitionStop.Execute(); }
             catch { }
+            // Before binning and the region: a format change can change their limits
+            camera.PixelFormat.Value = PixelFormat;
             camera.BinningSelector.Value = BinningSelectorEnums.All.ToString();
--- a/src/OpenRigs.Devices/TriggeredSpinnaker.bonsai
+++ b/src/OpenRigs.Devices/TriggeredSpinnaker.bonsai
@@ -15,6 +15,7 @@
         <Property Name="ColorProcessing" />
+        <Property Name="PixelFormat" />
       </Expression>
@@ -23,6 +24,7 @@
           <p1:Binning>1</p1:Binning>
+          <p1:PixelFormat>Mono8</p1:PixelFormat>
         </Combinator>
```
