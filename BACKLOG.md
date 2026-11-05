# Backlog

## Settings files written by rig-control, as open-rigs intends (plan, Pip, 5 Nov 2026)

open-rigs keeps parameters in files written before Bonsai and checked against their schemas (`specs/logging.md` section 6; `RigPath`, `SessionPath`, `TaskPath` in the reaction-time tutorial). The lab's rigs will do the same, with rig-control writing the task file. Not started. Words (Pip, 5 Nov 2026): an experiment is one workflow run; a session is the mouse's sitting, which can hold several. Each experiment has its own folder, `sub-<mouse>\ses-<SessionId>_<DateTime>`, named for the session it belongs to.

- **Three settings files, all in the experiment folder's `Schemas\`**: `Schemas_Rig_<DateTime>.json` (rig.yml as loaded), `Schemas_Task_<DateTime>.json` (the task as Bonsai deserialised it) and `Schemas_Experiment_<DateTime>.json` (open-rigs' `ExperimentSession` fields: subject, session, workflow, commit, repository URL; plus the rig, who launched, who pressed Go and every ExperimentControl parameter at Go). Written by `LogDataSchema` named `Schemas_Rig`, `Schemas_Task`, `Schemas_Experiment` (a log's folder is its name up to the first `_`). Replaces `RigSchema\` and `TaskSchema\`, which are named by `ExpectedFiles`, the two `check-workflow.ps1` lists, `lab_names.yml`'s av-dome `streams` (check-bonsai refuses `Schemas_*` until they are listed there) and descriptions (av-dome's `ExperimentManifest`, `task.yml`'s header, `FORMAT.md`'s example).
- **Each settings file expected on its own** (audit M2): one `Schemas/` entry is met by any one file, so `ExpectedFiles` and rig-sync take a prefix entry (`Schemas/Schemas_Task_`) that a file whose path starts with it meets, and workflows list all three.
- **The workflow writes the experiment file, in every case** (audit H1): a node in OpenRigs.Control writes it from `ControlState` at Go, which holds the exact parameter values (including those set by `ParameterDefault`) and who launched and pressed Go; the commit, repository and workflow come as the manifest reads them. No `ExperimentPath`, no second writer, and rig-control needs neither `open_rigs` nor pydantic. The manifest's `control` section keeps who and when, and the parameters move to the experiment file.
- **rig-control writes the task file at Go**, before relaying Go: the chosen task file with the form's edits, and the RNG seed drawn there when the file says `rngSeed: null`; validated against the project's `src\DataSchemas\*_task.json` (`jsonschema`, a new rig-control dependency), Go refused naming the field if it fails. Written to `C:\Coen-Lab-Local\rig-control\` (outside the project, so the manifest's `dirty` stays false and the rig's pull has nothing to conflict with); its path passed at launch as `--property:TaskPath`, only for a workflow exposing it, so rigs move over one at a time, and added to rig-control's `LAUNCH_OWN` so the form never offers it. `TaskFile` becomes the form's choice of task file at launch; `TrainingStage` stays an ExperimentControl parameter selecting a row, so the experiment file says which stage ran (audit M3).
- **The form** (audit M3, M4): every task field editable; each project's `config\rig-control.toml` ticks the ones shown by default. Lists (av-dome's `trainingStages`, `speakerLocations`) edit as text; the ticked single values are built first. For a workflow started outside rig-control, whose `TaskPath` rig-control did not choose, the task fields are read-only.
- **Without rig-control** (the editor by hand) `TaskPath` defaults to `config\task.yml`, and the experiment file is written as always.
- **The `RngSeed` event stays** (audit H2): with `rngSeed: null` in the file (an editor run) `InitialiseTrial` draws a seed nobody else records, and the logging spec (section 7.2) needs it recoverable.
- **Files read by path are kept or hashed** (audit M1): the Zapit conditions file the task names (`zapitConfigPath`, today an absolute machine path in `task.yml`) is copied beside the task file; rig.yml's calibration files (`meshMapPath`, `gammaLutPath`, `calibration\AuditoryStimuli`) and the mouse's spout positions (`calibration\LickSpoutPositions\<SubjectId>.csv`) go in `ExperimentManifest`'s `ConfigFiles` so their hashes are recorded.
- **av-dome**: `ApplyTaskOverrides` and its four parameters (`RewardSizeUl`, `ItiMin`, `ItiMax`, `Zapit`) go. Live parameters (`StepSize`; coen-behaviour-box's `ExposureTime`, `Gain`) stay ExperimentControl parameters; coen-behaviour-box's `RunLengthMins` stays one until it has a task schema.
- **Task files checked at commit** (audit M5): check-bonsai validates each `config\*.yml` against its own schema: `task*.yml` against `*_task.json`, `rig-template.yml` against the rig schema, `valve-calibration.yml` against `calibration.json`. Today nothing checks them before Bonsai reads them at Go, where the generated `DeserializeFromYaml` (no `IgnoreUnmatchedProperties`) throws on an unknown or misspelt key and the experiment ends early.
- **Retroactive renaming of recorded sessions, later** (Pip will rename a lot): one old to new mapping drives it (a `formerly:` beside each name in `lab_names.yml`: folders such as `RigSchema` to `Schemas_Rig`, logs such as `TrialSettings` to `BonsaiData_Trial`, trial columns), on the server copy (RDSS: renames only, never copy), rewriting each session's `manifest.json` file list in the same step; column renames rewrite CSV header lines, so backup and `--dry-run` before `--apply`. Inside a session folder is free; the `sub-<mouse>\ses-<id>_<DateTime>` levels and the server's `<RIG>/sub/ses` are hard-coded in about seven tools (ManifestWriter, LiveView's `SessionKey`, LoggedRows, rig-sync, rig-control, rig-setup's `rootPath` rule, ingest).

## Audit 2026-11-05, evening (settings-files plan and the Session -> Experiment rename): not changed

- rig-control accepting `SessionStoppedException` as well: only a workflow on packages before 0.1.1-coen.2 prints it, rig-setup deploys within 30 minutes, and an answered stop is classified `stopped` either way.
- `SessionOperators.cs` and OpenRigs.Manifest's internal `ControlSession` keep their names: outside Pip's mapping, and not seen by anyone running a rig.
- The third file's property named against open-rigs' `SessionPath`: moot, as the workflow writes the experiment file and no path is passed.
- Extending `ApplyTaskOverrides` instead of rig-control writing the task file: Pip chose the long-term route.
- The retroactive renaming's details (the seven tools, rig-sync's two-level rename detection): backlogged by Pip, not checked.

## Approved names, 5 Nov 2026 (`lab_names.yml`)

- **A package's dependency on Core names its own version, not Core's.** `build-package.ps1` passes `-p:Version` globally (`build\Common.csproj.targets` sets `<Version>` after each project's), so it reaches the referenced Core: Logging `0.1.1-coen.2` asks for Core `>= 0.1.1-coen.2`, which does not exist. Harmless while Bonsai restores only what `Bonsai.config` lists (Logging `bbox.6` asked for Core `bbox.6` the same way), wrong for anything that resolves dependencies. Fix: a lab `<LabVersion>` in each `.csproj` that `Common.csproj.targets` takes over its default, and no global `-p:Version`.
- **Not yet in av-dome's `BonsaiData_Trial`: `lickCount`, `responseTime` and the reward delivered per trial.** Each needs logic in the trial loop that only a session on the dome can check; what was delivered is already `RewardDelivered` in `BonsaiData_Async`. Add each to `lab_names.yml` when it is built.
- **Sessions recorded before av-dome took the new column names** (`TrialCnt`, `correctResponse`, `response`, `targetSide`, `BothSpouts`) no longer map in its `rig-control.toml`, which names only the new ones; `older_logs` covers `TrialSettings` only. Test sessions so far.
- **Async event names** (`RewardDelivered`, `RngSeed`, the controller's) are not in `lab_names.yml` yet.
- **A workflow's own `check-workflow.ps1` does not hold the names**: only `update-workflows.ps1` runs `check-bonsai.ps1` after it, so a name off the list shows as that workflow failing the hourly update, not before its author pushes.

## Test package changes on one rig first (Pip, 5 Nov 2026)

- A change pushed here reaches every lab workflow's ticked branches and `main` within the hour, gated only by each workflow's build check, which passes a change in behaviour. Proposed: a `dev` branch here whose `push.ps1` updates only targets marked in `profile-repos.json` (e.g. `"labPackages": "dev"` on one workflow branch, later a rig-setup tick), each target on exactly one channel so the hourly `main` run does not revert it; promotion fast-forwards `main` to `dev` and runs `push.ps1`. Every dev build gets a new version, since Bonsai caches by version, and `push.ps1` checks that promotion ships only versions that were on dev. Check first whether rig-setup deploys a ticked branch on one machine or every rig of the profile.

## Found 5 Nov 2026, on GREEN-DOME-1 (av-dome `pip/main-switchover` session ses-test-0852)

- Camera files are named differently by each workflow (Pip: "We need that camera naming abstracted and consistent across workflows"). Done in part (5 Nov 2026): Logging 0.1.0-bbox.6's `LogVideo` names files `Camera-X\Camera-X_<hour>.avi` and `.csv` through `FormatFileName`, and av-dome's branch includes it in place of its three pasted copies (whose FormatFileName dropped the hour); Devices 0.1.0-bbox.3's `TriggeredSpinnaker` carries `Gain`. coen-behaviour-box does not use `LogVideo`: it records with its own `RecordVideo` (FFmpeg H.264). Proposed, for Pip to confirm: a lab node, `RecordCameras`, generated from rig.yml as `StreamDevices` is: every `camera: <name>:` whose `Camera-<Name>.Data` the workflow publishes gets `Camera-<Name>\Camera-<Name>_<hour>.avi` and `.csv` (Seconds, FrameID, Timestamp, ExposureTime, Gain) through `RigDevices`, the naming LiveView, the manifest and HarpDevices share; the frame rate from rig.yml or the node; `check-workflow` failing a workflow that writes camera video another way.
- `LickSpoutStage-Main` gets no `_device.json` or register map: its rig.yml entry is two boards (`driverA`, COM4; `driverB`, COM5), and `RecordHarpDevices` reads one `portName` per device, so it never sees them, though both boards' data is logged (`LickSpoutStage-Main_DriverA_<reg>_<hour>.bin`). They are Harp StepperDriver boards (av-dome opens them with `Harp.StepperDriver`'s `Device`, which checks WhoAmI 1130). Proposed (5 Nov 2026), for Pip's names: two `stepperDriver:` entries in av-dome's rig.yml (`StepperDriver-SpoutA`, `-SpoutB`, publishing `.Data` and `.Commands`) in place of `lickSpoutStage:`, which every package here already handles; no multi-board rule. Done (HarpDevices 0.1.0-local.9, Manifest 0.1.0-local.16, LiveView 0.1.0-local.14 for `src\Shared`): TimestampGeneratorGen3 had a record but no map because `Harp.TimestampGeneratorGen3` 0.1.1, the latest release, carries no device.yml (upstream embeds it since c0e29e9, unreleased); `RegisterMaps` now uses the lab's copy of that release's file (a463d1d) for that version only, and the record names it in `register_map_from`. To ask upstream for a release.
- The Logging tests' `LogHarpDevice_SimulatedBehavior_*` fail 1 or 2 of 27 in most full runs and pass alone, on the code before Logging 0.1.0-bbox.5 as after (4 runs each, GREEN-DOME-1).

## Tell Tom Ryan: `tr-open-rigs` (8406573) does not build (4 Nov 2026)

Found while merging it into av-dome `pip/main-switchover`; fixed there, not on his branch. Both fail on his own pinned packages (`openrigs.* 0.0.0-ci17`, built from upstream `f313716`), so they are his branch's, not the fork's:

- `Logging` lost 9 edges in his merge `8406573` (git merged the node list and the edge list separately): RewardLineLeft/Right > Calibrated? > ArtefactJson > ValveCalibrationDO1/DO2, and RewardDelivered > LoggingPrefixCondition > Timestamp > LogData sit unconnected. `LogDataSchema`'s Zip then has one input ("No method overload found"), and the valve calibration and RewardDelivered logs would never be written. av-dome fix `d1cfa28` restores them as `tr-calibration-loop` wired them.
- `Logging > ExperimentLogs > TrialParameters` still stamps before building the row (`WithLatestTimestamp` then `new(Seconds as Seconds, Value.Item1...)`), so `GroupByTime` has no overload for it. His `c14dba2` fixed TrialSettings and Zapit this way, not this one. av-dome fix `169478b`.
- Also from his branch: NetMQ, AsyncIO, NaCl.Net and Bonsai.ZeroMQ dropped from `Bonsai.config` in the same merge (unused by av-dome, needed by the lab's LiveView, so kept on the switch-over branch).

Pip asked for this as a backlog item (4 Nov 2026), not a message: raise it with Tom when next in touch, nothing posted.

## Audit 2026-11-04, late evening (/audit-rigs, set B; rig-control left to its own session)

Changed (tests and docs only, no package version raised):

- HarpDevices' record tests waited on fixed sleeps and read `written` and `error` across threads (L2-L2): each wait polls for the write, the error or the file, up to 3 s.
- `patches\README.md` still said Logging 0.1.0-bbox.3 and listed its hash (S5-L1): bbox.4, its line and its hash.
- COEN-LAB.md said the green domes keep no rig.yml rig-control reads (S5-L2): GREEN-DOME-1's has `rigId: D1` since rig-setup 7e78905.
- The LiveView README credited coen-behaviour-box's session manifest with finding the workflow, and told a workflow to set `ConfigRoot` (S5-L3, S5-I2): `ExperimentManifest` through `src\Shared\WorkflowFile.cs`, and `ConfigRoot` left at its default. FORMAT.md's example writer is local.15 (S5-I1).

Not changed, do not raise again without new information:

- `RequestRegisterDump` reading WhoAmI itself about 1 s after Start, so a wrong-kind board stops in ready mode when the connection dump was missed (L2-L1): `CheckBoard` already confirms the USB serial at Start, so a wrong kind is a rig.yml filing a right serial under the wrong kind, caught on the first session with the record written first.
- `StoppedBy` before `StopReason` by store order, no lock or volatile (S2-I1): x64 never reorders stores, and every rig is x64.
- Done (HarpDevices local.9, LiveView local.14): the packed READMEs of HarpDevices local.8 and LiveView local.12 predated a1b9984's `licketySplit` and ConfigRoot notes (L2-I1).
- OpenRigs.Core's and the template's `C:\RigConfigs` default (S4-I2): upstream's, and both lab workflows set `ConfigRoot`.
- coen-av-arena takes `ucl-open-rigs` from upstream main unpinned while its row reads up to date (S1-I1): an old-template workflow with no OpenRigs node, nothing to keep in step.

Environment: the UCL-Open coen-behaviour-box clone on PIPOFFICERIG holds Control local.13 and no Logging bbox.4 while its Bonsai.config (f6983b3) names local.14, so rig-control's `test_bonsai.py` skips there (S2-I2); `.bonsai\Setup.ps1` in that clone brings it up, for the rig-control session.

## Audit 2026-11-04, evening (/audit-rigs, set A; rig-control left to its own session)

Changed (Manifest 0.1.0-local.15, Control 0.1.0-local.14):

- **A run that failed before the session folder existed had its error held back** (L1-M1, reproduced): `ManifestRecorder` kept it until the folder appeared, so a workflow whose only subscriber to the run was `ExperimentManifest` stood at Ready with a dead run, and a Go then made a folder for it. The error now ends the node at once (no folder, no session to record); a completion before the folder still waits for it. Tested.
- `ControlServer` sets `StoppedBy` before `StopReason` (L1-L1): the Manifest's stop poll fires on `StopReason` and then reads `StoppedBy`, so the other order could write the running stop without the person. Not reproduced; the window is two assignments.
- `scripts\update-workflows.ps1` says in the row when a repo with `src\DataSchemas` has no `regenerate-schemas` script, so its schemas stay stale at every pin move (S4-M1: av-dome's `pip/main-switchover`, whose `examples\rig.py` would also fail the step 7e9ed6f added; both for that branch, see Deferred).
- Docs: the Manifest README says `BonsaiData/` is the `ExpectedFiles` entry for `LogTrial` and `LogAsync` (L1-L2, S3-L2: the files carry the hour and wildcards are refused; a session with no trials still meets it through `BonsaiData_Async`); FORMAT.md says `closed: false` also comes from Bonsai's own writers, `BonsaiData_Async` open until disposal among them (L1-I); COEN-LAB.md names the four packages and the Logging and Core test projects (S5-L3).

Not changed, do not raise again without new information:

- `LogAsync` does not write the `Go` row rig-control's water count starts from (S2-H1): av-dome mirrors `ControlEvents` into `AsyncEvents`, so the row is there, and Logging stays independent of Control. The rule (a workflow with `LogAsync` sends its `ControlEvents` through `CreateAsyncEvent`, else rig-control records no water) belongs in claude-tools' bonsai-workflows skill and rig-control's README, not the package.
- Parameter limits not in `status` (S2-I1): a refused `set` names them; a second copy would drift.

Deferred, for Pip:

- Done on Pip's word (5 Nov 2026), in Logging 0.1.0-bbox.5 and LiveView 0.1.0-local.13 (CsvWriter's `AutoFlush` and `PublishRows`, `LoggedRows`, the `logs` stream): **`BonsaiData_Trial` and `BonsaiData_Async` reach disk about 4 KB at a time** (S3-M2): `OpenRigs.Logging.CsvWriter` rewraps Bonsai's own `CsvWriter`, which never flushes. The session page runs 15-25 trials behind, and a run killed past its stop deadline loses its unflushed tail, so `async_water_ml` undercounts with nothing saying so. A flush per row on `LogTrial`/`LogAsync` only (a few rows a second) is a patch to the bbox Logging package; a Harp log at full rate must not have it.
- A `Detail` starting with `"` swallows every later row of `BonsaiData_Async` in rig-control's `csv.reader` (S3-L3): `AsyncEvent.Clean` replaces commas and line breaks, not quotes; no sender today starts one. `Clean` could replace `"` with `'`, and rig-control read with `QUOTE_NONE`.
- av-dome `pip/main-switchover` (S4-M1): add `regenerate-schemas = "ucl_open_av_dome.regenerate:main"` to `[project.scripts]`, and make `examples\rig.py` write `config\rig-template.yml` (today it writes `.\local\UclOpenAvDomeRig.json` and calls `UclOpenAvDomeRig()` with no arguments, which throws) or delete it; with the script line alone, every hourly run would read FAILED.

## Audit 2026-11-04, afternoon (/audit-rigs, set C)

Changed (scripts and READMEs only, no package version raised):

- `scripts\hourly.ps1` writes the share's `<MACHINE>_WorkflowsFailed.txt` as UTF-8 without BOM from the log's text: the log is UTF-16 (`*>` under PowerShell 5.1), and the copied bytes reached ClaudioVisual as a NUL after every character. Probed: the log starts `FF FE`, the share copy `53 74`, no NUL bytes.
- `hourly.ps1` stops with `Stopped: fetch failed.` and `Exit 1` when the fetch of its own clone fails, as its other stops do; unchecked, the run went on with last hour's origin/main.
- `scripts\schedule.ps1` registers `%LOCALAPPDATA%\lib-open-rigs\self\scripts\hourly.ps1`, cloning `self` if missing, so the task runs what origin/main has, not the working copy's checkout or unpushed commits. hourly.ps1 resets that clone, its own file included, which is safe as PowerShell parses a `-File` script whole first. PIPOFFICERIG's task keeps the old path until `schedule.ps1` is run there again.
- `scripts\push.ps1` refuses, before pushing: uncommitted changes under `scripts\` (the parse check reads the files, the push sends HEAD); after `git fetch`, a `.nupkg` that a commit in `@{u}..HEAD` adds or changes while origin's history already holds that path; a lab `.nupkg` whose nuspec commit this clone lacks, or whose source there differs from the commit it was added onto (its parent), the 6d0b870 rebase case; and two versions of one package in `local_packages\`. `Directory.Build.props` and `.targets` now count as every lab package's source. Probed in a clone without a remote: today's tree passes, and each refusal fires on a recreated case.
- `scripts\update-workflows.ps1` stops a workflow's update when `dotnet tool restore` fails, naming it, instead of failing later in `regenerate-schemas` with no reason.
- Docs: the HarpDevices README lists `licketySplit` 1400; the HarpDevices and LiveView READMEs say only `check-workflow.ps1` maps `ConfigRoot` (to a temporary root) and rig-control refuses any other (rig-control 482dd5d); the LiveView README says the stream's `rigId` is the machine name; COEN-LAB.md and `hourly.ps1` say ClaudioVisual DMs the same failures and a `finished` over 26 hours old, and COEN-LAB.md lists push.ps1's refusals and the task's clone.

Not changed, do not raise again without new information:

- The Manifest recording a kill after Finished as `killed` (S3-M1): it is true, a kill can truncate the last file, and rig-sync alarms either way.
- No committed real-ManifestWriter sample for rig-setup's tests: it would make a fixture depend on another repository, and a probe of six cases agreed.

Deferred (other repos):

- coen-behaviour-box's `regenerate.py` should exit with bonsai.sgen's return code.

## Audit 2026-11-04, day (/audit-rigs, set B)

Changed (HarpDevices 0.1.0-local.8, LiveView 0.1.0-local.12, Manifest 0.1.0-local.14, all three also for `src\Shared`, above 6d0b870's local.7, local.11 and local.13 for the Lickety Split's WhoAmI):

- One rule for where rig.yml lives: `RigDevices.DefaultConfigRoot` and `RigDevices.PathFor` (`src\Shared\RigDevices.cs`), used by `HarpDevicePlan`, `LiveViewPlan`, `RecordHarpDevices` and `StreamDevices` (set C had to fix StreamDevices' default drifting from RecordHarpDevices').
- One rule for an empty YAML value: `RigDevices.Value` (null for null, `null`, `~`, empty or blank, else trimmed), called by `HarpRig.Setting` and LiveView's `liveView:` settings.
- LiveView streams a `behaviorBoard` only when `HarpRig.NotHarp` says it is a Harp device, the rule RecordHarpDevices records by and the manifest expects by; the recipe had its own HarpMessage check and ignored `portName`. Tested with a `portName: null` board whose data is HarpMessage, as HarpDevices' test does for records.
- `RecordHarpDevice` passes an error on the board's messages to the workflow (`observer.OnError`); before the session folder appeared it was dropped. Tested.
- A board whose WhoAmI came only after the session started has the note "The board reported no WhoAmI at connection, so its kind was checked only after the session started." in its record: a wrong kind then stops the workflow after Go, not before. Tested both ways.
- HarpDevices' dump-request test polls for the read for up to 2 s instead of a fixed 100 ms sleep.
- `scripts\hourly.ps1` runs `update-workflows.ps1` as a child process, as `push.ps1` does: called in-process, a script that did not parse left `$LASTEXITCODE` at git's 0 and the run was recorded as a pass (probed: a missing `}` gave `Exit 0`). The child gives `Exit 1` and the parser's error in the same log. No parse check of `scripts\*.ps1` was added to CI: `.github\workflows\OpenRigs.yml` is upstream's, not in COEN-LAB.md's table, and the lab's scripts are tested nowhere else.
- A controller's stop is written as soon as it comes, outcome still `running`, with `run.stop_reason` and `control.stopped_by` (`ManifestRecorder` looks for it every 0.25 s; `ManifestWriter.Stopping`, the same atomic write): rig-control kills a workflow slow to stop, and the end-of-run write that carried them never came. Tested; FORMAT.md says a `running` manifest may carry the stop, and its example writer is local.14.
- Docs: COEN-LAB.md and `hourly.ps1` name every reader of `<MACHINE>_WorkflowsUpdate.json` (rig-setup's rig-status report and Machines window at 3 h, ClaudioVisual's rig health DM at a day); the HarpDevices, LiveView and Manifest READMEs say what changed.

Not changed, do not raise again without new information:

- `src\Shared\WorkflowFile.cs`'s empty catches in the git readers (`:90`, `:141`, `:173`): reading .git must never stop a session, and the summaries say null when it cannot be read.

## Audit 2026-11-04, morning (/audit-rigs, set A)

Changed (Control 0.1.0-local.13, Manifest 0.1.0-local.12, HarpDevices 0.1.0-local.6, LiveView 0.1.0-local.10, the last two for `src\Shared`; pushed with e80399b):

- `AtomicFile.Write` (`src\Shared\Json.cs`) tries the rename up to 10 times, 50 ms apart: Windows refuses `File.Replace` while a reader holds the file without delete sharing (Python's `open()`; rig-control's agent polls `manifest.json` every second around the final write), and the final manifest was then lost. A Manifest test holds the file open for 200 ms during a write (fails without the retry). A final write that fails goes to stderr as well as the trace (`ManifestRecorder`).
- `RunEnd.AtDisposal` says `stopped` only when the controller's stop set `StopReason`, as FORMAT.md says, not for any Stopping phase; `ControlSession.Stopping` is gone. Tested.
- `ExpectedFiles` drops a leading `./` (rig-sync matches `RigSchema/x.json`) and leaves out, with a note, an entry that is rooted or holds `..`, as it does a wildcard. Tested.
- `ControlServer` reads every frame of a request and refuses more than one ("one frame per request, got N"): a second frame left unread made the reply throw, which ended the session. Tested with a two-frame status (fails without the change).
- A Control test that Go and a command are answered within 1 s while a subscriber spends 3 s on each. The two-servers test waits for the first to answer before starting the second (they raced to bind).
- `scripts\hourly.ps1` writes `K:\config\<MACHINE>\<MACHINE>_WorkflowsUpdate.json` every run (`machine`, `started`, `finished`, `exitCode`, `lastPass`; atomic, UTF-8 without BOM; a failed share write is logged, not fatal), which rig-setup's rig-status report flags when `finished` is over 3 h old or `exitCode` is not 0: nothing noticed a task that stopped running. COEN-LAB.md says so, and no longer says rig-setup's Machines window shows `WorkflowsFailed.txt` (PIPOFFICERIG has no heartbeat, so it never did; set B above says it did).
- Docs: FORMAT.md's example writer is Manifest local.12 and `expected[].path` has no `./`; the Control and Manifest READMEs say what changed.

Not changed, do not raise again without new information: none.

## Audit 2026-11-04 (/audit-rigs, set C)

Changed (LiveView 0.1.0-local.9, Manifest 0.1.0-local.11; Pip: "Implement everything recommended as you go"):

- `push.ps1` refuses a package whose source (its folder, its READMEs aside, and `..\Shared\` it compiles) changed after its `.nupkg` was committed with `<Version>` unraised: it pushed and shipped nothing. It found Manifest local.10 built before 7b02057's Shared change, so Manifest is local.11 (no behaviour change).
- `build-package.ps1` refuses a version committed at any time (`git log --all`), not only one in the index: a version set back by hand rebuilt a file a rig may hold cached.
- The hourly task runs `update-workflows.ps1` from its own clone, `%LOCALAPPDATA%\lib-open-rigs\self`, set to `origin/main` each hour: unpushed commits in the working copy failed three runs in a row on 3 Nov, and a branch checked out there would have pinned every workflow to it. `push.ps1` still runs from the working copy. The task's limit is three hours (rerun `scripts\schedule.ps1` once to take it).
- A ticked branch gone from GitHub is `gone`, not `FAILED`, as rig-setup reads it, until Repos takes the tick off.
- A dependency folder with NuGet's fourth version part (`openal.redist.2.0.7.0`) is found by the dependency walk and copy, as `Get-SetupGap` already found it.
- `StreamDevices`' `ConfigRoot` defaults to `C:\Coen-Lab-Local\RigConfigs`, as `RecordHarpDevices`' (unset, it streamed nothing).
- FORMAT.md defines `completed.unmet` and rig-sync's recount of `expected[].found` (rig-setup, same round); the Manifest and HarpDevices READMEs say rig-sync copies a run that did not complete with a note and an alarm (rig-setup ddff734); the LiveView README says `ConfigRoot` can be mapped (7b02057); COEN-LAB.md says a ticked branch gone from GitHub is `gone`.
- After Phase 5 (`8162fcb`): `check-bonsai.ps1` compiles `src\Extensions` as Bonsai does, leaving out a folder under `src` with a project of its own; av-dome `pip/main-switchover`'s gamma calibration (MathNet, in its own `Extensions.csproj`) failed every hourly run. A nested folder with a space in its name is passed as `%20` (Phase 5 WARN).

Not changed, do not raise again without new information:

- `update-workflows.ps1` writing a version downgrade (`:298` tests only `-ne`): `build-package.ps1` now refuses any version committed before, so a downgrade cannot be built.
- OpenRigs.Core asking for YamlDotNet 16.0.0 where the lab's packages ask 16.3.0: Core is open-rigs' code, and every lab workflow also installs a package that requires 16.3.0; an upstream issue if it ever matters.

## Audit 2026-11-03, night (/audit-rigs, set B)

Changed (Control 0.1.0-local.12, HarpDevices 0.1.0-local.4, LiveView 0.1.0-local.7; Manifest stays local.10; Pip: "Implement everything recommended as you go"):

- A failed hourly run lived only in `%LOCALAPPDATA%\lib-open-rigs\update-workflows-failed.log`: `scripts\hourly.ps1` copies it to `K:\config\<MACHINE>\<MACHINE>_WorkflowsFailed.txt` and deletes that after a passing run; rig-setup's Machines window shows it.
- `update-workflows.ps1` refuses to run with uncommitted `scripts\` edits (they ran against every workflow); matches package ids in any case (av-dome's `tr-open-rigs` branch writes `openrigs.*`), in `Get-ConfigVersion`, the missing check and `install-package.ps1`'s rewrite, which writes the lab's casing; waits at most 40 min for the lock, under the task's 1 h limit; prints a `not kept` row for a repo with main left alone and no branch ticked; `-Manifest` is `-Rules`.
- `ReportMetric` formats in the invariant culture, as `ParameterDefault` does (a rig in another culture wrote `0,5`).
- `HarpRig.MapToWrite` is the one rule for which register map a record writes (reported WhoAmI first, else rig.yml's kind); `RecordHarpDevice` uses it and notes when neither decodes; `HarpDevicePlan` notes a Commands subject that is not HarpMessage, so no dump is asked for.
- `LiveViewPlan` keeps the default for a `liveView` setting left null (`null`, `~` or empty, as `model_dump` writes it); the `RigId` comment at :138 went in with it.
- Later the same night (HarpDevices 0.1.0-local.5, LiveView 0.1.0-local.8; Pip: "I will need it now though, so do it"): a workflow property could not be mapped onto `RecordHarpDevices` or `StreamDevices`, so coen-behaviour-box's `--property:ConfigRoot` reached `RigConfig` only. Bonsai's build looks a mapping onto a group up among its inner workflow's externalized properties, and the generated workflow had none ("The specified property 'ConfigRoot' was not found in the nested workflow"). `GroupNode.Externalize` adds one mapping of `ConfigRoot` onto the settings node, made in both constructors and kept by `Generate`; the HarpDevices tests map one onto it, build, set it as `--property` does, and check the editor's copy keeps one mapping (each fails without the change).
- Docs: the HarpDevices README says it has run on GEEK-BBOX1 since 2 Nov 2026; COEN-LAB.md says how the hourly task reaches `K:\config\profile-repos.json` on PIPOFFICERIG and where a failed run goes.

Not changed, do not raise again without new information:

- `expected: false` renamed `listed`: FORMAT.md defines it, and rig-sync and the Manifest tests read it.
- Boards in the flat `RigConfigs\<M>.yml` layout: the approved-names check flags the older layout, and a Shared change raises three packages.
- `UsbSerials.cs:35` falling back to the port name: weighed in set B's verification and left as it is.
- The version labels in Manifest's comments: a comment in `src\` moves the open-rigs pin; no Manifest raise this round.

## Audit 2026-11-03, evening (/audit-rigs, set A again)

Changed (Control 0.1.0-local.11, HarpDevices 0.1.0-local.3; Pip: "Implement everything recommended as you go"):

- `ControlServer`: Go takes its parameter snapshot, checks every value against the limits as they stand and sets `GoRequested` under one lock (the gap a `ParameterDefault` could land in, deferred this morning), and refuses Go, naming the parameter, while a value set before `ParameterMaximum` lowered the limit is out of range (probed: Exposure 10000 set, maximum 5000 learned, Go recorded 10000; an empty value, one nothing set, is left to the workflow's default as before, since coen-behaviour-box's ExposureTime and Gain are empty until rig.yml fills them); the socket's own receive and send errors end the sequence through the observer, as a failed bind does, where unhandled on the server's thread they ended Bonsai with no file closed. The downstream `OnNext` stays outside the catch, so a throw in a node is fixed in that node.
- `RecordHarpDevice`: a board that reported no WhoAmI at all gets the map of the kind rig.yml names, with a note, since the manifest expects that map from rig.yml's kind (`HarpRig.RegisterMapFor`) and the session was held back every night otherwise; a board of another kind stops the workflow before this.
- `FORMAT.md`: `run.items` is `null` in every `running` write (so a manifest rig-sync finishes as `killed` keeps `null`); the example session folder has its project level.
- `scripts\hourly.ps1` holds the hourly task's body, and `schedule.ps1` registers a task that runs it, so a change to the run arrives with a pull: PIPOFFICERIG's task still ran the 2 Nov registration, with no failed-run log and success whatever happened; registered again on PIPOFFICERIG at about 19:50 on 3 Nov 2026 (first run 20:00, which stopped on this repo's unpushed commits, as it should). The morning's claim that the task's exit code reaches Task Scheduler's history was wrong: `conhost --headless` reports 0 whatever the code (measured), so `update-workflows-failed.log` is the record of a failure.
- COEN-LAB.md: only B1 is in a rig.yml; the dome, opto and acute codes are planned.

Not changed, do not raise again without new information:

- Work downstream of Go, a set or a command on the request thread (a stop sent meanwhile waits): see rig-control's BACKLOG; the README says so.
- A comment at `LiveViewPlan.cs:138` saying the stream's `RigId` is the machine name on purpose (rig.yml's `rigId` is `B1`; the live view subscribes by machine): a comment in `src\` moves every workflow's open-rigs pin; with the next LiveView change.

## Audit 2026-11-03 (/audit-rigs, set A)

Changed (tests, docs and scripts; no package raised): the Manifest tests named every session folder the old `_date-` way, so `SessionSection` was never tested on the name rigs write since badd9ca (both forms checked now, with `session.id`, `subject` and `started`); `FORMAT.md` says `ended-early` carries a sentence in `error`, and its example launch is a rig's (`C:\Github\Coen-Lab`, `--start`); the schedule's task ends with the script's exit code and keeps every failed run in `update-workflows-failed.log` (the hourly log was overwritten and the task read as succeeded while av-dome's setup had failed every hour since 2 Nov); the open-rigs pin ignores a README under `src\` (1e7ed99 moved every workflow's pin for one); the setup-gap message names a NuGet token the machine lacks as a cause; docs that still said `Video 0.1.0-bbox.1`, "Lab Packages On Main" (rig-setup's tick is Leave Main Alone, inverted, since 5a8b6c4) and `desk-personal` for PIPOFFICERIG.

Not changed, do not raise again without new information:

- `ControlServer` sets `GoRequested` just outside the lock that takes Go's parameter snapshot (`ControlServer.cs:551`): a `ParameterDefault` landing in that gap would show a value differing from Go's, but coen-behaviour-box fires it once, 1 s after Start; a fix forces a Control raise and a push to every workflow for a race no workflow can hit. Take it with the next Control change.
- A `Gain` set above the camera's maximum is answered ok, then stops the workflow in Ready: coen-behaviour-box gives `Gain` no `<Maximum>` and no `ParameterMaximum`; the fix is that workflow's (a maximum, or `ParameterMaximum` from the camera's `Gain.Max`), and upstream `TriggeredSpinnaker` could clamp. Raised in the audit report, not here.
- `ParameterDefault` and `ParameterMaximum` throw before ControlServer has subscribed: fails loudly; coen-behaviour-box's `DelaySubscription PT1S` covers it.
- A `ExperimentStoppedException` reaching the run input through the camera's PropertyMapping (since a92cde7) could write `error` instead of `stopped`: the include's terminal subscribes first, so teardown disposes the manifest before the camera chain sees the error; not seen.
- `Matches` counts a device's `<Name>_device.json` and `<Name>.yml` towards its folder under `auto` alone: coen-behaviour-box runs `auto;harp;RigSchema/`, whose Harp kinds are all in the WhoAmI table, so no live workflow is held back or passed wrongly. The `FORMAT.md` rule (a register map never stands in for data) is the intent; a code change waits for a workflow that hits it.
- `OpenRigs.Core` declares YamlDotNet 16.0.0 where every other package declares 16.3.0: NuGet resolves coen-behaviour-box to 16.3.0; bump Core's reference at its next pack; superseded by set C's item above (upstream's code).

## Audit 2 Nov 2026 (rig.yml-driven Harp identity, dump and register map): not changed

Fixed in the same round (HarpDevices 0.1.0-local.2, Manifest 0.1.0-local.9): the Harp-device rule was written twice, and the two disagreed on a `portName: null` and on the register map, which the manifest expected for every Harp device although `RecordHarpDevices` writes it only when a loaded package has the board's WhoAmI, so every session of a rig with a Lickety Split or Pico board would have been held back by rig-sync; both now use `src\Shared\HarpRig.cs`, and the map is expected only when a loaded package decodes the kind. The record was written as soon as FirmwareVersionLow arrived, but a board's dump interleaves core and app registers, so the device name, Harp serial and unique id came from the dump at connection, or were null; it now waits until the dump has been quiet for 0.2 s, still bounded by `DumpTimeout`. A device with no `.Commands` said "No register dump came within 5 s" when none was asked for; it now writes at once and says so. A board of the wrong kind stopped the workflow only after Go; the dump at connection now stops it in ready mode. The port name came from WMI's display name, where rig-setup reads the registry's `Device Parameters\PortName`; the registry comes first now. A throw in `update-workflows.ps1` escaped the hourly task's redirect, so the log stopped after "Packages:" with no reason; it is written to the log, with exit 1. Dropped: stopping at Start when a rig.yml entry with a `portName` has no matching subject, which would also stop on a `spare:` board that is ignored on purpose.

- The WhoAmI table copied from open-rigs' `devices\harp.py`: there the values are Python ClassVars, in no schema C# can read; the numbers were checked equal.
- No BoardClockSet gate on the dump: ControlServer refuses Go until Ready (`ControlServer.cs:484`), and Ready needs a triggered frame, so the clock is set first.
- The `keep` subscription swallowing errors: the same error reaches the per-session path.
- `Thread.Sleep` in the HarpDevices tests: about 3x margins. The Manifest tests' stand-in HarpMessage, now the real one from Harp.Behavior: no clash.
- WMI listing unplugged ports: checked, it lists present ports only.

## Audit 1 Nov 2026 (round 7 of 7: deployment and recovery)

Changed (scripts only, no package): `update-workflows.ps1` took a branch as set up once `.bonsai\Packages` existed, so a setup killed halfway, or one that found no network after `main`'s packages were copied in, was never run again and the branch's check failed every hour; it now asks for Bonsai.exe and a folder for every package `Bonsai.config` names (NuGet's `2.0.7.0` for a config's `2.0.7` included). `push.ps1` pushed a raised `<Version>` that was never built and reported every workflow up to date (round 6, 0a90299); it refuses until each package's version is in `local_packages\`.

Checked and clean: a workflow's `.bonsai\Setup.ps1` waits for Bonsai's restore when its output is captured, as rig-setup's run captures it (a copy of coen-behaviour-box with two package folders removed: both back when it exited, 4 s).

## Audit 1 Nov 2026 (round 6 of 7: live acceptance, with a person at the rig)

Changed (Control 0.1.0-local.9, Manifest 0.1.0-local.5): the manifest is the session's record, and it did not say who ran the session. rig-control kept a record of its own in gui-pyrat's `session_log.json` on the share, which the first real run could not write ("No lab server configured": the lab adapter never set up gui-pyrat's store), and which nothing read. On review it was dropped for the manifest, which travels with the data and is written however the session was started: `go` and `stop` may name a person (`by`, and on `go` `launched_by`, each `{"id", "name"}` or a name alone), ControlState keeps them as `LaunchedBy`, `GoBy` and `StoppedBy`, `status` reports them, and the manifest's `control` section writes them (session-manifest SPEC, version 2, same day). A controller that names nobody leaves them null; an older ControlServer ignores the fields.

## Audit 1 Nov 2026 (round 5 of 5: simplicity, unity, open-rigs compatibility and style): not changed

Fixed in the same round (Control 0.1.0-local.8, LiveView 0.1.0-local.5, Manifest 0.1.0-local.4, no behaviour changed): a package's folder could not move into open-rigs, since its `.csproj` set a licence expression beside open-rigs' licence file (`dotnet pack` there: NU5033, reproduced against open-rigs 77094a4); the shared settings are in `Directory.Build.props` now, as open-rigs keeps them in `build\`, and the Control tests check every `.csproj` holds only what an open-rigs one does. LiveView and Manifest each had their own copy of the git reader, the workflow file, the build-context lookup, the settings node and its property filter, and the rig.yml walk (one copy read Bonsai's relaunch arguments, the other not); they compile `src\Shared\` in. Manifest no longer depends on Control: it reads `ControlState.Current` by name, checked against the real one by its tests. Manifest's helpers (`Git`, `Json`, `ManifestWriter`...) were public; internal now. Every public member has a summary (Control had 40 without, LiveView 22, Manifest 83), and a missing one stops the build. A package's id is its folder's name. `OnGo` was missing from Control's README.

- Console test harnesses where open-rigs uses MSTest (`src\OpenRigs.Logging.Tests`): moving a package upstream moves its tests into open-rigs' test projects and their helpers (`OpenRigs.Tests`) anyway; converting here first would be done twice.
- Manifest's own JSON writer (80 lines) rather than Newtonsoft.Json: Manifest no longer loads Newtonsoft at all, and the overnight copy reads the files byte for byte as written now.
- `Nullable` disabled here where open-rigs enables it: the code has no annotations, and under open-rigs' build `OpenRigs.Control` packed with no warnings (77094a4).
- Stream names `<Kind-Instance>.video` and `.encoder` rather than open-rigs' examples' `video/face`: they name the `<Kind-Instance>.Data` subjects they come from, and rig-setup's live view and rig-control's window read them; rig-setup cannot be changed from a rig.
- The control protocol's keys in snake_case (`subject_id`) where OpenRigs.Streaming's header is camelCase (`rigId`): the protocol is read by Python and written to Python's records, and changing it now is a break in two repositories for no reader's gain.

## Audit 1 Nov 2026 (round 2 of 5: the whole repository): not changed

Raised by the independent audit of every package, script and document here and kept as they are; do not raise again without new information. Fixed in the same round: ExperimentManifest's failed write threw back up ControlServer's thread and closed Bonsai with nothing finished (Manifest 0.1.0-local.3 ends the node with the error instead), a stop or bind error waited 2 s on the server's own thread (Control 0.1.0-local.7), `set` took `NaN` for a parameter with limits, the live view named `src\src\main.bonsai` for a relative launch path where the manifest names `src\main.bonsai` (LiveView 0.1.0-local.4), a second update-workflows run exited 0 claiming the running one covered its push (it now waits), and Control and LiveView had no tests here (`tests\OpenRigs.Control.Tests`, `tests\OpenRigs.LiveView.Tests`).

- ControlServer does not catch an exception thrown back into its emit of Go or a command. Once `OnNext` has thrown, Rx detaches the observer and swallows any later `OnError` (probed), so the server cannot end the workflow cleanly from there; the fix belongs in the node that threw, as ExperimentManifest's was.
- `status` carries no parameter limits (`Minimum`, `Maximum`, `WholeNumber`): nothing reads them yet, and a refusal names the range. Decided in rig-control's round 4: not shown; `set`'s refusal names the range.
- `ControlState.Go`, `StopReason` and `FinishedAt` are written once and read without the lock: each is one reference or flag, and a stale read cannot give a wrong outcome.
- `ConfigRootNote` looks only for a property named `ConfigRoot`: that is OpenRigs' RigConfig convention.
- A workflow written by an un-indented `XmlWriter` does not reload (Bonsai's group reader); the editor indents, and no lab tool writes workflows.
- The hourly update-workflows meeting the Lab PIN hook: PIPOFFICERIG is a `desk-developer` machine (personal access), which rig-setup leaves without the hooks. A run on a rig (push.ps1 there) asks for the PIN, as any commit there does.
- Two ControlServers in one workflow: the second fails to bind and stops the workflow, naming the port.
- The rig.yml device walk and the build-context lookup copied in LiveView and Manifest, two readers of the workflow's commit, and console test harnesses where open-rigs uses MSTest (`src\OpenRigs.Logging.Tests`): round 5's lens, simplicity and open-rigs style.
- Rebuilding a package to compare its DLL with the committed one: builds embed the source path, so the hashes differ whatever the code.
