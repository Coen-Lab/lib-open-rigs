# open-rigs

> **Coen-Lab/lib-open-rigs** is the Coen Lab's fork: its `main` is what every lab workflow runs. How the lab uses it is in claude-tools [`docs/architecture/COEN-LAB.md`](https://github.com/Coen-Lab/claude-tools/blob/main/docs/architecture/COEN-LAB.md).

Repository for the open-rigs ecosystem: shared rig schemas (Python), Bonsai acquisition operators (.NET), and a Copier template used to scaffold new experiment repositories.

Full documentation, including tutorials and API reference, is published at **https://open-rigs.github.io/open-rigs** (deployed on release).

## Prerequisites

| Tool | Purpose |
|------|---------|
| [Git](https://git-scm.com/) | Version control |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Building the C# packages and the DocFX site |
| [Python 3.11+](https://www.python.org/downloads/) with [`uv`](https://docs.astral.sh/uv/) | Python package, schema generation, tests |
| [Bonsai](https://bonsai-rx.org/) | Optional, only needed to render workflow SVGs locally |

## Build the docs locally

```bash
dotnet tool restore
dotnet docfx docs/docfx.json --serve
```

DocFX will print a `http://localhost:8080`-style URL. Open it to preview the site.

To also render workflow images (requires Bonsai on Windows), run `pwsh docs/build.ps1` instead.

## Adding a rig or a device

A workflow builds its rig from device pieces, one include per device instance ([Building Blocks](docs/articles/building-blocks.md)).

- **A new rig of devices the lab already has:** give the rig its `rig.yml` with an entry per device, then in its workflow include `ReadLabRig` (set `ConfigRoot`), `ReadyGate`, one clock (`ClockFromTimestampGenerator` or `ClockFromBoard`), one piece per device with its `Name` set, `CameraTriggers` if it has a camera, `SpeakerMixer` if it has a speaker array and `ProjectorDisplay` if it has a projector. Set `PieceStages` to the stages the pieces announce (`SpeakerMixer` announces `MixerStarted`). Run `scripts\check-workflow.ps1` (`-Workflow <file>` for one other than `main.bonsai`) and the workflow's tests: `workflow_lint.hardware_problems` names anything missing or misnamed. No new hardware code. When a second task runs on the same rig kind, move its pieces into a rig list include beside `DomeHardware`, and add it to `RIG_LISTS` and `SIGNATURES` in `workflow_lint.py`.
- **A new kind of device:** add its part to the rig model (`src/open_rigs/components/`, then `uv run regenerate-lab-rig`), and write its piece in `OpenRigs.Devices`: one externalised `Name` mapped to `RigDevice`, its `AsyncSubject` and its log (`LogHarpDevice` for a Harp device), `CommandsSubjectName` and `DataSubjectName` defaulting empty or `UNMAPPED` (never a real subject's name), a refusal at Start naming what is missing, and an `AnnounceStage` into `ReadyStages` if a task must wait for it. Teach `workflow_lint` its file and subjects, and add it to the acceptance fixture in `tests/rigs/test_workflow_lint.py`.

## Python workflow

```bash
uv sync
uv run pytest
```

## Licence

`open_rigs.zapit` (`src/open_rigs/zapit/`, the `zapit` extra) is LGPL-2.1-only, upstream Zapit's terms; the rest is BSD-3-Clause. See `THIRD-PARTY-NOTICES.md`.
