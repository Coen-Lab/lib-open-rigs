# Patched builds of other projects' packages

`local_packages\` also carries builds of packages with fixes the lab needs that are not released yet. Each has a version of its own, distinct from any upstream one, so a restore never confuses the two.

## OpenRigs.Core, Devices, Streaming and Video 0.1.1-coen.1; Logging 0.1.1-coen.2

Until 5 Nov 2026 these were `0.1.0-bbox.N` (first built for coen-behaviour-box) and the lab's own packages `0.1.0-local.N`. Every lab package is now `0.1.1-coen.N` (`COEN-LAB.md`); the history below names the versions as they were.

Built because open-rigs has not published a release yet (ucl-open/ucl-open#12), from this repository's own `src\`: upstream branch `rig-config-resolver` at `3788bfb` (main plus the per-machine config operators `ResolveRigConfigDirectory`, `ReadRigConfig` and `ResolveRigFile`) and two fixes, both reported upstream, which are commits on this repository's `main`:

| Commit | Fixes | Upstream |
|---|---|---|
| `8deeb9c` | `TriggeredSpinnaker` records a cropped frame when the camera's stored region is smaller than the sensor: the region is reset to full frame after binning | ucl-open/ucl-open#66 |
| `ee4a8a4` | Bonsai crashes when a frame waits in the Zip, because `ChunkData` is read after Spinnaker releases the image: the chunk fields are copied inside the camera callback (five at first; `Gain` added in Devices 0.1.0-bbox.3, without which `LogVideo` could not build against it). This changes only the `OpenRigs.Devices` `TriggeredSpinnaker` include; the `OpenRigs.Video.TriggeredSpinnaker` operator already copies `FrameID` and `Timestamp` before the Zip | ucl-open/ucl-open#67 |

Core and Devices 0.1.0-bbox.2 and Logging 0.1.0-bbox.3 (4 Nov 2026) add upstream `main` at `f313716`, merged into this repository's `main`: the calibration loop (dispense calibration, balance operators, curve builder), per-line pulse outputs on DO1 to DO3, camera trigger enable flags (`EnableTrigger1` off by default) and LogData CSV columns named by field. av-dome's `tr-open-rigs` needs them. `LogDataSchema.bonsai` is upstream's (deferred write); this fork's only change there had been an editor sample path.

Logging and Streaming 0.1.0-bbox.2 (2 Nov 2026) also name the experiment folder `ses-<Session>_<DateTime>`, without `date-`, as agreed on ucl-open/roadmap#37: `LogController` writes it, and `StreamController`'s session key matches it. The time is still UTC. They have a `<Version>` in their `.csproj`, so `scripts\build-package.ps1` builds them.

Video 0.1.0-bbox.3 (2 Nov 2026, a92cde7 and 19e39cb) is a lab change rather than a patch: a new `ExposureTime` or `Gain` goes to the open camera, and `VideoChunkData` carries both per frame. It has a `<Version>` in its `.csproj`, so `scripts\build-package.ps1` builds it.

Logging 0.1.0-bbox.4 (4 Nov 2026, 4e79e7f) is a lab change rather than a patch: `LogTrial`, `LogAsync` and `CreateAsyncEvent`, the lab's two experiment files (`COEN-LAB.md`). 0.1.0-bbox.5 (5 Nov 2026) adds CsvWriter's `AutoFlush` and `PublishRows` and the `LoggedRows` source, both on in `LogTrial` and `LogAsync`, so their rows are on disk and streamed as they are written. Those two properties are the one part of it inside an upstream operator, `CsvWriter` (upstream's since `f313716`), and neither Bonsai's CsvWriter nor open-rigs has anything like them (Bonsai's `Buffered` moves writing to another thread, not to disk). Kept here off by default (Pip, 5 Nov 2026), to be offered upstream: not yet reported.

Logging 0.1.1-coen.2 (5 Nov 2026) adds `CsvWriter.ColumnNames`, the header's columns for a type without writing, for check-bonsai's hold of `BonsaiData_Trial` to `data_dictionary.yml`.

Logging 0.1.0-bbox.6 (5 Nov 2026) fixes upstream's `LogVideo`: its two inline `FormatFileName` copies left out the chunk start, so files were `<LogName>\<LogName>.avi` and an experiment crossing the hour opened the same file twice. It now includes `OpenRigs.Logging:FormatFileName.bonsai` as `LogRawVideo` does, giving `<LogName>\<LogName>_<DateTime>.avi` and `.csv`; a test holds it equal to `LogRawVideo` apart from its frame selector and CSV columns. An upstream file, to be offered upstream: not yet reported.

Core and Devices have a `<Version>` in their `.csproj` since bbox.2, so `scripts\build-package.ps1` builds them like the rest.

SHA-256 of the packages as committed (built with .NET SDK 8.0.424 on PIPOFFICERIG, 5 Nov 2026):

```
e4c568a295c91b91a2553331d8d64f953135030307a196a5f50ed0b18c1dca02  OpenRigs.Core.0.1.1-coen.1.nupkg
64feb703e03e1538b03311db342724e80da45c2a2dd7307e33d6e1c86ea0a4bb  OpenRigs.Devices.0.1.1-coen.1.nupkg
0a2826832467fa77e3fef2fb25f385c747f05e96681b6877aa098bef90bf39be  OpenRigs.Logging.0.1.1-coen.2.nupkg
e0d07b700ad1a6d18090e1a7713e51bf23f8c029df1c491db50e98667522f73d  OpenRigs.Video.0.1.1-coen.1.nupkg
e7ec0d808241666ed45db825e4abe8546bdbc083fef8afac296204e9f0719622  OpenRigs.Streaming.0.1.1-coen.1.nupkg
```

`OpenRigs.Streaming` is upstream's `src/OpenRigs.Streaming` at the base commit, unchanged by either patch. It declares Bonsai.Dsp and Bonsai.Vision 2.9.0 and Bonsai.Scripting.Expressions 2.10.0; Bonsai restores only what a workflow's `Bonsai.config` lists, at the listed versions, so a workflow on 2.8.0 runs it on its 2.8.0 assemblies.

## Bonsai.FFmpeg 0.2.0-uniquepipe

[bonsai-rx/ffmpeg](https://github.com/bonsai-rx/ffmpeg) tag `v0.2.0` (`ee403aeca3d6ecb214487a5c41d94b2f2b541526`) plus `bonsai-ffmpeg-unique-pipe.patch`, which names each recording's pipe after the file stem plus a GUID, so a Start while the previous recording's ffmpeg is still draining no longer fails with "All pipe instances are busy." The DLL was built with `dotnet build -c Release` and put into the nuget.org `Bonsai.FFmpeg` 0.2.0 package (signature removed), first in Coen-Lab/hw-behaviour-box. DLL SHA-256 `ecb50c0d0618e5e31969d9c7a45a1b82a7f362db97a5cc831e884087433a5403`; package SHA-256 `43e124e7608ff0ec32ced7edf5b5fd2ff34c4072d027ccd75c1d76fa1e950d00`.

The lab's workflows record video with it rather than Bonsai.Vision's `VideoWriter` (what `OpenRigs.Logging:LogVideo` uses), because that writer encodes FMP4 on one thread inside Bonsai: on coen-behaviour-box's Intel N150 at 1440 x 1080, 50 Hz it fell behind whenever the CPU was busy, queued about 1 GB of frames a minute in RAM, and crashed a 30-minute experiment at about 23 minutes (hw-behaviour-box 25a0bcd). FFmpeg encodes H.264 on Intel Quick Sync with flat memory.

## Switching to upstream

When open-rigs publishes a release containing both fixes and the config operators, or Bonsai.FFmpeg one with a unique pipe name, delete the patched package here, and in each workflow that uses it set the released version in `.bonsai\Bonsai.config` and remove its copy from `local_packages\`. `scripts\update-workflows.ps1` then leaves that package alone, since it only handles packages in this repository's `local_packages\`. Delete `bonsai-ffmpeg-unique-pipe.patch` with the FFmpeg package.
