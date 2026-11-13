# Patched builds of other projects' packages

`local_packages\` also carries builds of packages with fixes the lab needs that are not released yet. Each has a version of its own, distinct from any upstream one, so a restore never confuses the two.

## OpenRigs.Core, Devices, Logging, Streaming and Video

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
| `5b6baaa` | Logging (coen.2) | `CsvWriter.ColumnNames`, the header's columns for a type without writing, for the hold of `BonsaiData_Trial` to the lab's names (app-rig-control's `check-workflow-names.ps1` since 6 Nov 2026) | Lab-only |
| `9ce8bc9` | Core, Devices (coen.2) | `ManualBalance` beside `SerialBalance`, built on `ManualWeighing`, `ManualReadings` and `ApplyBalancePort` (claude-tools `DECISIONS.md`, 6 Nov 2026) | Lab-only (Pip: "Nothing of Tom's") |
| `f309da2` | Logging (coen-lab.6) | `LogVideo`'s CSV drops `ExposureTime` and `Gain`, leaving `Seconds`, `FrameID` and `Timestamp`: both are fixed while recording and recorded once in the experiment's settings (Pip, 10 Nov 2026) | Lab-only |
| `0bacc27`, `f2f8802`, `90558a7` | Video (coen-lab.4 to .6) | `UsbCamera`, a USB camera that is not the rig's own: exposure live in microseconds as `ExposureTime`, and a missing frame ends the stream only after none for 5 s or 4 exposures (Pip, 10 Nov 2026) | Lab-only |
| `cffa26c` | Video (coen-lab.7), Devices (coen-lab.5) | `TriggeredSpinnaker` sets `PixelFormat` each time the camera opens, Mono8 when not set, and the include exposes it: a format stored on the camera was used before (BLUE-OPTO-1's were Mono16) | Not reported (`BACKLOG.md`) |
| `e137284` | Video (coen-lab.8) | A blank or null `PixelFormat` is Mono8, as coen-qc-preflight-rig reads rig.yml, not an opaque Spinnaker error | With `cffa26c` |

`OpenRigs.Streaming` is otherwise upstream's `src/OpenRigs.Streaming` at the base commit. It declares Bonsai.Dsp and Bonsai.Vision 2.9.0 and Bonsai.Scripting.Expressions 2.10.0; Bonsai restores only what a workflow's `Bonsai.config` lists, at the listed versions, so a workflow on 2.8.0 runs it on its 2.8.0 assemblies.

## Bonsai.FFmpeg 0.2.0-uniquepipe

[bonsai-rx/ffmpeg](https://github.com/bonsai-rx/ffmpeg) tag `v0.2.0` (`ee403aeca3d6ecb214487a5c41d94b2f2b541526`) plus `bonsai-ffmpeg-unique-pipe.patch`, which names each recording's pipe after the file stem plus a GUID, so a Start while the previous recording's ffmpeg is still draining no longer fails with "All pipe instances are busy." The DLL was built with `dotnet build -c Release` and put into the nuget.org `Bonsai.FFmpeg` 0.2.0 package (signature removed), first in Coen-Lab/hw-behaviour-box. DLL SHA-256 `ecb50c0d0618e5e31969d9c7a45a1b82a7f362db97a5cc831e884087433a5403`; package SHA-256 `43e124e7608ff0ec32ced7edf5b5fd2ff34c4072d027ccd75c1d76fa1e950d00`.

The lab's workflows record video with it rather than Bonsai.Vision's `VideoWriter` (what `OpenRigs.Logging:LogVideo` uses), because that writer encodes FMP4 on one thread inside Bonsai: on coen-behaviour-box's Intel N150 at 1440 x 1080, 50 Hz it fell behind whenever the CPU was busy, queued about 1 GB of frames a minute in RAM, and crashed a 30-minute experiment at about 23 minutes (hw-behaviour-box 25a0bcd). FFmpeg encodes H.264 on Intel Quick Sync with flat memory.

## Switching to upstream

When open-rigs publishes a release in which every row of the table above is upstream, with the config operators, or Bonsai.FFmpeg one with a unique pipe name, delete the patched package here, and in each workflow that uses it set the released version in `.bonsai\Bonsai.config` and remove its copy from `local_packages\`. app-rig-setup's `workflows\update-workflows.ps1` then leaves that package alone, since it only handles packages in this repository's `local_packages\`. Delete `bonsai-ffmpeg-unique-pipe.patch` with the FFmpeg package.
