# Decisions

Why the lab's infrastructure is shaped as it is, for audits. Each entry: what was decided, by whom and when, the reasons, and what was rejected and why. A later decision that reverses one adds an entry and marks the old one superseded; entries are never edited away.

## 2026-11-05: Lab infrastructure leaves Pip's computer (PIPOFFICERIG)

Decided by Pip with Claude, 5 Nov 2026. Built in four stages, in this order; each stage's commits name it.

**Principle.** No lab check, update, deploy gate or scheduled job may depend on Pip's own computer. Pip: "This is not really sustainable... I don't want things depending on my computer." The hourly task "lib-open-rigs update-workflows" ran only while Pip was signed in to PIPOFFICERIG, and its health files (`K:\config\<M>\<M>_WorkflowsUpdate.json`, `_WorkflowsFailed.txt`) existed only to notice when it stopped.

### Stage 1: descriptive script names (done 5 Nov 2026)

- `scripts\push.ps1` is `scripts\push-and-update-workflows.ps1`; `scripts\check-bonsai.ps1` is `scripts\check-workflow-builds.ps1`. Pip: "audit file names to be more descriptive. E.g. \"push\" is not clear enough". Old commit messages keep the old names; rig-setup tests 07 and 18 and ClaudioVisual's `test_rig_health.py` keep `check-bonsai` in fixture text, since those tests go in stage 4.

### Stage 2: the names live on K:, not in git

- `K:\config\device-dictionary.json` (device names as `Kind: [Instances]`, rig.yml settings keys) and `K:\config\data-dictionary.json` (log streams, experiment settings, trial columns with `type` and `required`), edited through rig-setup's Repos page behind the Lab PIN with a change log, as `rollouts.json` is.
- **One lab-wide superset per file, no per-rig lists.** The check only ever asks "is this name listed", and only lab-wide trial columns are required. Pip: "Since these are \"allowed\" names, should it not just be one superset every rig checks against?" Rejected: lists per kind of rig, and keyed by repo ("many kinds will be on multiple rigs").
- **On K:, not in a repo.** Once the meaning of the data (stage 3) moves to the workflows and the manifest, the names list no longer decides how data is read, so it needs no versioning with the code. Accepted cost: nothing off the lab network (CI, outside collaborators) checks names.
- **Checked where it runs, not where it is written.** At deploy on the rig by rig-setup (a failure keeps the previous deployed version and is flagged in the Machines window and ClaudioVisual) and by rig-control at load, which **blocks** the session on an unlisted name; with K: unreachable it uses the last good copy on the rig and says so. Pip: "Can't it just check when loading with rig control?" then "Block". Rejected: a CI names check and a pre-push hook (hooks are per clone and bypassable).

### Stage 3: meaning belongs to each workflow and each session

- Codes (`values`), `unit` and `formerly` leave the dictionary. Each workflow repo defines its own (av-dome owns its `trialType` codes) and each session saves them in its `manifest.json`; `open_rigs.trials` reads them from the session, falling back to the workflow repo at the session's commit for older sessions. Pip: "And why trial type couldn't be different? Aren't we just checking names?" Then `data_dictionary.yml` and `data_dictionary.py` leave this repo.
- Built 6 Nov 2026 (Pip: "Build as planned"). A workflow repo's file is `schemas/trial-columns.yml`: every column it writes, with `type` as well as `values`, `unit`, `formerly` and `description`, so an experiment reads without K: (on jacuzzi); rig-control's names check holds its names and types to `K:\config\data-dictionary.json`, which keeps names, `type`, `required` and `description` only. ExperimentManifest's `TrialColumns` (Manifest 0.1.1-coen.4, blank by default: a workflow without trials sets nothing) copies it into `manifest.json` as `trial_columns` at Start, so the first write has it. Text in the manifest, unlike `config_files`: the 2 Nov rule (settings live once, in the experiment's settings JSON) is about not copying what the experiment already holds, and nothing else in it holds the columns. Only av-dome logs trials (coen-behaviour-box has no `LogTrial`). `open_rigs.trials.read(experiment, repo=None)` raises when neither the manifest nor `repo` has the columns, rather than reading every column as text.

### Stage 4: one updater, run by whoever pushes; the hourly task retires

- `push-and-update-workflows.ps1` is the only updater, run by whoever pushes here from any machine with Bonsai, .NET, write access to ucl-open and, on a rig, the Lab PIN. It writes `K:\config\lab-package-versions.json`; rig-setup flags a workflow behind it, rig-control **warns** (never blocks) on behind.
- Package versions stay committed in each workflow repo, so each repo runs on its own. Pip, rejecting rigs that load lab packages from a shared path: "But doing this basically creates repos that don't run independently?"
- The repo list stays `K:\config\profile-repos.json`, not a repo. Pip: "people from any pc might change repo list at any time. How can it be a repo?"
- Rejected: a GitHub Actions or GitHub App updater (needs a bot login and the ucl-open owners' approval; too complicated), and a scheduled task on a rig.
- Then `scripts\hourly.ps1` and `scripts\schedule.ps1` go, the task is unregistered on PIPOFFICERIG, and the `*_Workflows*` files on K: are deleted.
- Built 6 Nov 2026 (Pip: "Build as planned"). `update-workflows.ps1` writes `lab-package-versions.json` beside `-RepoList` at the start of each run, before any workflow is updated, so a run that stops part way shows as workflows behind it rather than as nothing. `-Rules` is gone rather than given another default: `rig-setup-rules.json`'s repos (claude-tools, lib-keyring, rig-control) are none of them workflows. Behind means a set-up clone's `Bonsai.config` versions of the listed packages, or its `pyproject.toml` open-rigs pin, differ from the file; a branch left alone (Leave Main Alone) is never behind. Accepted cost: a repository added on the Repos page, or a branch ticked, waits for the next run, and its rigs show it behind until then; `update-workflows.ps1` alone (no push) is the fix.

### Stage 2, first: the share's config folder holds machines in one place (6 Nov 2026)

- `K:\config\` holds only lab-wide files (`profile-repos.json`, `data-dictionary.json`, `device-dictionary.json`, `rollouts.json`), `_backups\` and `machines\`. Every machine's files are under `config\machines\<MACHINE>\`, including rig-control's card (`rig-control.json`) and its session summary (`<MACHINE>_Sessions.json`, before this in `config\rig-control\sessions\<MACHINE>.json`). Pip, looking at `K:\config`: "This should be tidied up--not a mix of rig names and a bunch of other random folders & files".
- Lab-wide files stay at the top of `config\`, not in a `config\lab\` folder: four or five files never need a folder, and `profile-repos.json`, read by every rig every 30 minutes, keeps its path.
- `config\login-used\` (one stale Lab PIN stamp from 23 Oct; the stamps live in `K:\logins\login-used\`) was deleted, with a backup in `config\_backups\`. `config\RigTransfer\` (ASUS chipset and Serial IO drivers copied by hand on 20 Aug, used by no code) moved to `K:\setup\drivers\`.
- Order: the code in rig-setup, rig-control, ClaudioVisual and `scripts\hourly.ps1` first, then the folders moved; a rig that had not yet taken rig-setup's update wrote to its old folder for up to 30 minutes, merged in afterwards.

### Stage 2, the deploy check flags and does not roll back (6 Nov 2026; supersedes "a failure keeps the previous deployed version" above)

- rig-setup runs rig-control's `check-workflow-names.ps1` on a rig's clone after it takes a new version (once per commit and lists, recorded beside the clone), and a failure is an outstanding row in the Machines window and ClaudioVisual's rig health. The new version stays in place, so that workflow cannot launch (rig-control blocks it) until the names are added or the workflow is changed. Pip chose this over rolling back: the check needs the new version's packages, so it can only run after the pull and Setup, and a rollback would have to reset the clone, set up the old packages again and refuse that commit on every later update.

**Out of scope, noted.** Rigs restore ucl-open packages with a token on Pip's personal GitHub account (rig-setup-rules.json, until ucl-open publishes to nuget.org, ucl-open/ucl-open#12). pip-hub's Monday rota emails also run on PIPOFFICERIG.

## 2026-11-06: A workflow sends; rig-control decides how it is shown

Pip: "bonsai repo just sends everything, and visualisation should be decided by a file that is either part of rig control, or a separate git repo". Until now a project's `config\rig-control.toml` held both its contract and its presentation, so changing a chart meant a commit in the Bonsai repo.

- **The workflow keeps only what it declares:** its parameter and command names on the `ExperimentControl` include, its trial columns' meaning in `schemas\trial-columns.yml` (stage 3 above), and its task files. Nothing about how it is shown: labels, units, help and choices are the window's text, so they are the view's, not the include's (putting them on the include would have changed the Bonsai package to carry window text).
- **rig-control keeps the whole view:** `views\<project>.toml` in rig-control, named by the project's folder (a branch copy `<repo>@<branch>` takes its repository's unless it has its own): layout, tiles, current-trial line, chart tabs, parameter form, command pad, and the map from the workflow's names to what is shown (`[trials]`, `[async]`, `[live]`, `[task]`). The views ship with the code that draws them, so a chart kind and every view that uses it change together; the agent reads them from its own clone, so a view reaches a rig when it pulls rig-control.
- **Not a separate repo,** for now: views only mean something to rig-control's renderer, and rigs already pull rig-control. Split them out if another lab needs to edit views without rig-control, at the open-rigs move.
- **Drift is said, never refused:** at each experiment the agent checks the view's parameters and commands against the include and its `[trials]` columns against the schema; what does not match is in the `view` reply and one amber line on the Experiment page. `tests\test_views.py` checks every view against any local clone of its workflow.
- **No view:** a workflow whose schema lists the core trial columns gets a default view (the lab's tiles, Performance and Across Experiments); any other shows its parameters, commands and metrics as declared. Trial type names come from the schema's `values` when a view names none.
- **Moved:** av-dome's `config\rig-control.toml` (`pip/main-switchover`) became rig-control's `views\av-dome.toml`; av-dome's copy is deleted once the rigs run the rig-control that reads `views\`.

## 2026-11-06: Calibrations are the machine's, run from one page, each kind by its own workflow

Pip: calibrations should be "all ... part of one interface in rig control", and "it needs to learn the valve channels etc. from rig.yml". Until now open-rigs' dispense calibration (`OpenRigs.Core:DispenseCalibration.bonsai`, merged 3 Nov 2026) ran only from av-dome's `src\CalibrateValve.bonsai` (Tom, 8 Oct 2026), by hand in Bonsai with its ports typed in, reading nothing from rig.yml.

- **The machine's, not an experiment's:** an artefact sits in `calibration\` beside the rig's rig.yml and every workflow on that rig reads it, so a calibration is never part of an experiment workflow or its form.
- **One workflow per kind, each in its own ucl-open repository** (valves: `ucl-open/coen-calibrate-valve`; Tom's `qc-calibrate-valve` scaffold left as his), since each kind needs its own hardware and packages. Each is an open-rigs project under `ExperimentControl`, so rig-control launches, watches and stops it like an experiment.
- **It reads the rig's own rig.yml**, only the entries it needs (the valve workflow: `behaviorBoard: main:`'s port, `outputPulseEnable` and `outputs`), never a rig.yml or schema of its own: a strict deserializer of its own would refuse av-dome's other entries.
- **One page in rig-control** (Setup's Calibrations), from `calibrations.toml` there: every kind, one row per line, each line's curve and history, and Calibrate launching that kind's workflow. Every other launch says, never refuses, when a calibrated line's curve is missing, old or another machine's.

## 2026-11-06: The valve calibration stays Bonsai, with no C# of its own

Pip asked "this is a bonsai workflow... should it be?" before it had run on hardware. Decided by Pip with Claude, 6 Nov 2026. Supersedes the entry above's "it reads the rig's own rig.yml": rig-control reads it instead.

- **Bonsai, not Python in rig-control.** The pulses go through the same `BehaviorBoard`/`PulseController` path a session fires, and Tom's `DispenseCalibration` is the procedure. The only hardware drivers are Bonsai's: `open_rigs.devices` holds config schemas, not drivers. Rejected: a Python procedure in rig-control, which would need a second Harp writer and balance reader that could drift from the Bonsai ones. The artefact (a `CalibrationCurve`, points only) was never the obstacle.
- **The lab workflow repo holds no C#.** `coen-calibrate-valve` carried 530 lines: a rig.yml reader, the line check and a manual balance. Pip: "Manual balance would then be a \"device\" in open-rigs effectively?" Yes:
  - `OpenRigs.Devices:ManualBalance.bonsai` beside `SerialBalance.bonsai`. It publishes typed weights as `WeightMeasurement` readings, built on `ManualWeighing`/`ManualReadings` and `ApplyBalancePort` (settings for a COM port or `manual`) in OpenRigs.Core. This is a lab patch on the open-rigs packages. Pip chose not to take it to Tom's open-rigs ("Nothing of Tom's").
  - `GoByName` (who pressed Go) is in OpenRigs.Control. `DescribeCalibrationCurve` is in OpenRigs.Core.
  - rig-control reads rig.yml, refuses a line it does not enable, and launches with the board's `PortName`, `OutputPulseEnable`, the line's `Channel` and artefact `FileName`, and `Notes` as launch properties. That replaces the launch-poll-set sequence. (6 Nov 2026: `Notes` dropped from the launch, rig-control `8b3129b`; the balance is read by eye or off the camera.)

## 2026-11-07: Bonsai measures, rig-control computes

Pip, choosing where the projector gamma fit lives: "rig-control, Python". Then: "So you would suggest we modify coen-calibrate-valve in the same way? The repo saves the data and rig-control analyses it?" Yes. Supersedes the entry above where it keeps `DispenseCalibration`'s fit in OpenRigs.Core.

- **A calibration's workflow drives the hardware and display and writes its raw measurements** with `CsvWriter`, nothing more: no C# in its `src\Extensions`, no fit. rig-control's agent fits them once the run completes and writes the artefacts (`calibrations.py` `fit_run`, tested with pytest). Rejected: C# in the workflow (untested, C# 5) and a lab package in this repo (a release through `push-and-update-workflows` for one calibration's arithmetic).
- **Valve:** `coen-calibrate-valve` copies `DispenseCalibration`'s bursts and weighings (`RunCalibration`, `PrimeAndWeighBaseline`, `FireAndWeighSample`, still using `PulseBurst` and `WeighStable` from OpenRigs.Core) and writes `calibration\<FileName>-weights.csv`; rig-control's `rebuild` makes the `CalibrationCurve` JSON that `CalibratedPulse` reads, as before. Pip: "Would it be easier to build that from existing stuff rather than some new package?" The copy no longer follows upstream's `DispenseCalibration`, which this repo keeps unchanged; the balance side was lab-only already. `SelectCalibrationPoints`, `BuildCalibrationCurve`, `FitCalibrationCurve`, `WriteCalibrationArtefact` and `DescribeCalibrationCurve` are no longer used by any lab workflow.
- **Projector gamma:** `coen-calibrate-projector` writes `calibration\gamma-ramp.csv`; rig-control writes `gammalut.bmp` and `gammalut.json` (its `WriteGammaLookup.cs` ported to Python and deleted).

## 2026-11-07: QC repositories are coen-qc-<verb>-<thing>; the pre-training check is the preflight

Pip, naming the pre-training rig check: "I think we should distinguish from experimental workflows clearly though... and calibrate seems good for that. But maybe we insert qc into all of them", after Thom's `ucl-open/qc-calibrate-valve` scaffold. Then, of "check": "Is ""Check"" right word?"; preflight was chosen over checkout, verify, signoff and the rest. "Do it."

- **Every quality-control repository is `coen-qc-<verb>-<thing>`:** `coen-calibrate-valve` and `coen-calibrate-projector` were renamed `coen-qc-calibrate-valve` and `coen-qc-calibrate-projector` (GitHub redirects the old URLs), and the new check is `ucl-open/coen-qc-preflight-rig`. The entries above keep the old names as they were written.
- **The name is the rule:** rig-setup gives a `qc` or `calibrat` repository no desktop shortcut, and rig-control never offers one in its Workflow list (`is_qc_name`); rig-setup's `retiredRepos` offers each rig's clone under an old name for quarantine.
- **The preflight checks that every device rig.yml declares works, before training:** one workflow for every rig, its device branches started from rig.yml, its rows judged in Python. rig-control refuses a launch without a pass in 18 h once its agent runs with `--preflight`; the `test` mouse is exempt, and a Lab PIN with a reason lets one mouse through, once per rig per UK day.
