# The Coen Lab's open-rigs

This fork's `main` is what every Coen Lab workflow runs: upstream open-rigs (branch `rig-config-resolver` at `3788bfb`, NeuroThom's per-machine config operators), plus the lab's fixes and packages. Each lab change is a commit on `main`; a change meant for upstream also gets a branch of its own for its pull request.

| On `main` | What it is |
|---|---|
| `src/open_rigs/core/rig.py` | `rig_id` (`rigId` in rig.yml): optional, the rig's code (B1 GEEK-BBOX1, D1 and D2 the green domes, O1 and O2 the blue opto rigs, A1 PINK-ACUTE), the end of every session ID |
| `8deeb9c`, `ee4a8a4` | `TriggeredSpinnaker` fixes, ucl-open/ucl-open#66 and #67 ([`patches/README.md`](patches/README.md)) |
| [`OpenRigs.Control`](src/OpenRigs.Control/README.md) | Holds a workflow ready (hardware armed, live view streaming, nothing recorded), answers [rig-control](https://github.com/Coen-Lab/rig-control) over ZeroMQ, and releases the session on Go |
| [`OpenRigs.Manifest`](src/OpenRigs.Manifest/README.md) | `SessionManifest` writes `manifest.json` into each session folder, with nothing wired to it. The format is its [`FORMAT.md`](src/OpenRigs.Manifest/FORMAT.md) |
| [`OpenRigs.HarpDevices`](src/OpenRigs.HarpDevices/README.md) | `RecordHarpDevices` checks every Harp device in rig.yml at Start by its `usbSerial`, and writes each device's registers and register map into each session |
| [`OpenRigs.LiveView`](src/OpenRigs.LiveView/README.md) | `StreamDevices` streams every device in rig.yml that the workflow publishes, with the workflow's repository and commit |
| `src/Shared/` | Code compiled into LiveView, Manifest and HarpDevices as internal code, so none depends on another |
| `local_packages/` | One `.nupkg` per package: the four above, the patched `OpenRigs.Core`, `Devices`, `Logging`, `Streaming`, `Video` `0.1.0-bbox.1`, and `Bonsai.FFmpeg` `0.2.0-uniquepipe` ([`patches/README.md`](patches/README.md), licences in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)) |

They moved here from coen-openrigs-custom on 2 Nov 2026.

## Every lab workflow runs this

A lab workflow is any repository on [rig-setup](https://github.com/Coen-Lab/rig-setup), for any profile, or in [`workflows.txt`](workflows.txt), that is a Bonsai project in the open-rigs layout (`.bonsai\Bonsai.config` and `src\main.bonsai`). `scripts\update-workflows.ps1` keeps each one's `main`, and each branch ticked for it in rig-setup (or named after it in `workflows.txt`, where `!main` leaves `main` alone), on this repository:

- every package in `local_packages\` at the version here, with what it depends on as coen-behaviour-box's `Bonsai.config` has it, and the `../local_packages/` NuGet source;
- open-rigs (Python) from this repository: an `open-rigs` git source in `pyproject.toml` is pinned to the last commit here that changed `src\`, `pyproject.toml` or `local_packages\`, `uv.lock` is relocked, and `regenerate-schemas` is run if the workflow has it. The schema and the Bonsai packages then come from the same commit.

A branch that changed is checked with its own `scripts\check-workflow.ps1` (else `scripts\check-bonsai.ps1` here) after its Bonsai setup. If the check passes, what the script wrote is committed and pushed; rig-setup's 30-minute update takes `main` to the rigs. If it fails, nothing is committed and the table at the end names the branch and why; the next run tries again. It works in clones of its own under `%LOCALAPPDATA%\lib-open-rigs\clones`, so nobody's working copy is touched.

It runs after every push with `scripts\push.ps1`, and hourly on PIPOFFICERIG (`scripts\schedule.ps1`; log in `%LOCALAPPDATA%\lib-open-rigs\update-workflows.log`). The checks build workflows and run their operators offline, but a workflow that loads `Bonsai.Spinnaker` needs the Spinnaker SDK, so this runs on a lab machine, not in CI.

## Changing a lab package

1. Raise `<Version>` in `src\<Package>\<Package>.csproj`. NuGet caches by id and version on every machine, so `build-package.ps1` refuses a version already committed. open-rigs' build stamps `42.42.42-dev0` on a plain build; `build-package.ps1` passes the `.csproj`'s version.
2. Run its tests, a console program that needs only the .NET SDK and exits 0 when every check passes: `dotnet run --project src\OpenRigs.Control.Tests` (likewise `LiveView`, `Manifest`, `HarpDevices`).
3. Build it into `local_packages\`, replacing the old version: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-package.ps1 -Package OpenRigs.LiveView`
4. Commit, and push with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\push.ps1` instead of `git push`. It refuses a raised version with no `.nupkg`, then updates every workflow.

Every public type and member of a lab package has a `<summary>`; a missing one stops the build (CS1591 in each `.csproj`). The packages build against Bonsai.Core 2.9.0 with current C#; only a workflow's own `src\Extensions`, which Bonsai compiles, must be C# 5.

Pushing `main` prints "Bypassed rule violations": the org ruleset asks for pull requests, and the lab commits on `main`.
