# Patched builds of other projects' packages

`local_packages\` also carries builds of packages with fixes the lab needs that are not released yet. Each has a version of its own, distinct from any upstream one, so a restore never confuses the two.

## OpenRigs.Core, Devices, Video 0.1.0-bbox.1; Logging, Streaming 0.1.0-bbox.2

Built because open-rigs has not published a release yet (ucl-open/ucl-open#12), from this repository's own `src\`: upstream branch `rig-config-resolver` at `3788bfb` (main plus the per-machine config operators `ResolveRigConfigDirectory`, `ReadRigConfig` and `ResolveRigFile`) and two fixes, both reported upstream, which are commits on this repository's `main`:

| Commit | Fixes | Upstream |
|---|---|---|
| `8deeb9c` | `TriggeredSpinnaker` records a cropped frame when the camera's stored region is smaller than the sensor: the region is reset to full frame after binning | ucl-open/ucl-open#66 |
| `ee4a8a4` | Bonsai crashes when a frame waits in the Zip, because `ChunkData` is read after Spinnaker releases the image: the five chunk fields are copied inside the camera callback. This changes only the `OpenRigs.Devices` `TriggeredSpinnaker` include; the `OpenRigs.Video.TriggeredSpinnaker` operator already copies `FrameID` and `Timestamp` before the Zip | ucl-open/ucl-open#67 |

Logging and Streaming 0.1.0-bbox.2 (2 Nov 2026) also name the session folder `ses-<Session>_<DateTime>`, without `date-`, as agreed on ucl-open/roadmap#37: `LogController` writes it, and `StreamController`'s session key matches it. The time is still UTC. They have a `<Version>` in their `.csproj`, so `scripts\build-package.ps1` builds them.

The packages as committed were built from the same source as patches, in coen-openrigs-custom before it moved here. To rebuild, raise the version (a rebuild is a different file even from the same source, as nupkg hashes are not deterministic), delete the old `.nupkg` files and run:

```powershell
foreach ($p in 'Core','Devices','Logging','Streaming','Video') {
    dotnet pack "src/OpenRigs.$p/OpenRigs.$p.csproj" -c Release -p:Version=0.1.0-bbox.2 -o local_packages
}
```

SHA-256 of the packages as committed (built with .NET SDK 8.0.424 on 28 Oct 2026; Logging and Streaming bbox.2 on 2 Nov 2026):

```
8d08926fadd8bcff1c9450c663ec81a8f11b644bcdbbb7204fdef9d729666ef9  OpenRigs.Core.0.1.0-bbox.1.nupkg
82ae3d13854ff68010ba59c81d65f6d9be7d0039fc39a2912f3c8ca567c9cbca  OpenRigs.Devices.0.1.0-bbox.1.nupkg
78074525e8228158268f6dd302d39595f76ab4bf027f335cc89cbb25ca2a0463  OpenRigs.Logging.0.1.0-bbox.2.nupkg
26b0e832a0b4bd83adf8533cf62c12f6995b9b8a6761c604cd62b6522661fe2a  OpenRigs.Video.0.1.0-bbox.1.nupkg
bd7fd1177f3ce29f35c90072eb1ac800e04314bf5a8bf2556a729a79be70c462  OpenRigs.Streaming.0.1.0-bbox.2.nupkg
```

`OpenRigs.Streaming` is upstream's `src/OpenRigs.Streaming` at the base commit, unchanged by either patch. It declares Bonsai.Dsp and Bonsai.Vision 2.9.0 and Bonsai.Scripting.Expressions 2.10.0; Bonsai restores only what a workflow's `Bonsai.config` lists, at the listed versions, so a workflow on 2.8.0 runs it on its 2.8.0 assemblies.

## Bonsai.FFmpeg 0.2.0-uniquepipe

[bonsai-rx/ffmpeg](https://github.com/bonsai-rx/ffmpeg) tag `v0.2.0` (`ee403aeca3d6ecb214487a5c41d94b2f2b541526`) plus `bonsai-ffmpeg-unique-pipe.patch`, which names each recording's pipe after the file stem plus a GUID, so a Start while the previous recording's ffmpeg is still draining no longer fails with "All pipe instances are busy." The DLL was built with `dotnet build -c Release` and put into the nuget.org `Bonsai.FFmpeg` 0.2.0 package (signature removed), first in Coen-Lab/hw-behaviour-box. DLL SHA-256 `ecb50c0d0618e5e31969d9c7a45a1b82a7f362db97a5cc831e884087433a5403`; package SHA-256 `43e124e7608ff0ec32ced7edf5b5fd2ff34c4072d027ccd75c1d76fa1e950d00`.

The lab's workflows record video with it rather than Bonsai.Vision's `VideoWriter` (what `OpenRigs.Logging:LogVideo` uses), because that writer encodes FMP4 on one thread inside Bonsai: on coen-behaviour-box's Intel N150 at 1440 x 1080, 50 Hz it fell behind whenever the CPU was busy, queued about 1 GB of frames a minute in RAM, and crashed a 30-minute session at about 23 minutes (hw-behaviour-box 25a0bcd). FFmpeg encodes H.264 on Intel Quick Sync with flat memory.

## Switching to upstream

When open-rigs publishes a release containing both fixes and the config operators, or Bonsai.FFmpeg one with a unique pipe name, delete the patched package here, and in each workflow that uses it set the released version in `.bonsai\Bonsai.config` and remove its copy from `local_packages\`. `scripts\update-workflows.ps1` then leaves that package alone, since it only handles packages in this repository's `local_packages\`. Delete `bonsai-ffmpeg-unique-pipe.patch` with the FFmpeg package.
