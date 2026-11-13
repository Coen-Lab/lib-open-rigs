# Backlog

## Deferred: proposed upstream

- `TriggeredSpinnaker` `PixelFormat` (cffa26c, Video 0.1.1-coen-lab.7 and Devices .5; blank means Mono8 since Video .8, audit 11 Nov 2026): upstream's operator never sets the format, so a camera opens in whatever format it stored (BLUE-OPTO-1's were Mono16), and LogVideo writes 8-bit frames only. To offer to ucl-open as an issue with this diff (`patches/README.md` lists it); not yet reported.

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

## Audit 2026-11-11 (/audit-rigs, sets A, B and C: the seams, Control, Manifest, HarpDevices, LiveView, Video, scripts)

Changed (Manifest 0.1.1-coen-lab.13, Control .10, Video .8; open-rigs' pin moves with `behaviour_box.py`):

- Manifest: `finished` is set only once the end-of-run write returns, the first end kept as the run's, so a write that failed (the file held by a reader) is tried again at disposal (L1-M1). Bonsai disposes the node straight after its error, so that second try comes about half a second later, not at the run's end: a reader holding `manifest.json` longer still leaves it `running` until rig-sync's stale rule. Making the node outlive its error is a design change, left. FORMAT.md's stop sentence says `running` was the stop's write only to coen-lab.11 (L1-L1).
- Control: a socket whose bind failed is disposed, so NetMQ's cleanup returns (L1-L2), tested.
- Video: a blank `PixelFormat` is Mono8 (L2-L1). `behaviour_box.py`'s `pixel_format` is `Mono8` only: LogVideo's FFmpeg writer takes 8-bit frames, and Mono16 failed after Go (L2-M1). The wiring `device` description names the names check (L2-L2).
- LiveView README: `MotorStopped` comes as `{State, Changed}` (L2-H1; rig-control's reader dropped every one).
- `patches/README.md` is a table of the lab's changes to upstream packages (UsbCamera and PixelFormat added), with no version numbers, and `Assert-LabPackagesShippable` refuses a `.nupkg` it names that `local_packages\` lacks (L3-M1); COEN-LAB.md's versions and rigIds (L3-L1, S4-L4); a package's inputs are the Shared files it compiles, not the folder (L3-L2, reversing the 8 Nov "Not changed": Video counted every Shared change too).

Not changed, do not raise again without new information:

- `LaunchedBy` and `GoBy` set after `GoRequested` outside the lock (open since 3 Nov): `status` is answered on the same single control thread that runs `go`, so no reply falls between them (L1-I1).

Deferred to the workflow repos (read-only here): coen-behaviour-box and coen-dome-navigation map no `PixelFormat` onto their cameras, so rig.yml's `pixelFormat` is accepted and ignored there (S4-M1); av-dome has no `schemas\board-lines.yml`, so no stepper's wiring is checked (S4-L2); the LiveView templates are never deserialized in a test, as that needs the Harp packages (L2-L5). Video has no test project for `UsbCamera`'s helpers (L2-L4).

## Queued package fixes shipped, 8 Nov 2026

Changed (one raise each: Control 0.1.1-coen-lab.9, Manifest 12, HarpDevices 8, LiveView 7; open-rigs' pin moves with `behaviour_box.py`):

- Manifest: `run.final` (set A's L1-L2). The workflow's Finished and the controller's stop are written as they come, the run still going, as `completed` or `stopped` with `run.final: false` (every `running` write has `false` too); the end-of-run write sets `run.final: true`; absent (older manifests) means final. In FORMAT.md and the README. rig-control keeps an editor's Bonsai open while the manifest says `running` or `run.final` is false, and rig-sync treats `final: false` as `running` until stale (their own changes, the same day).
- Control: two parameters, or two commands, with one name stop the workflow at Start, naming both by position (S2-L2); `SetPhase(Recording)` or `SetPhase(Finished)` before Go throws, so `ReportPhase` stops the workflow rather than status lying (S2-I1); each entry of `status`'s `commands` carries `taken`, the times the command was taken, counted in `Handle` (the night's S2-L2), in the README's protocol table; `ExperimentSettings`' summary says the repository is read when it is subscribed, not at Go (L1-I1).
- HarpDevices: a `board-lines.yml` whose top level is not a mapping, or whose board's entry is not a list, refuses ("must be a list of entries") instead of turning the check off (L2-L1); a `ReadLines` that throws goes to the "could not be read" refusal, not out of `HarpDevicePlan.Create` (L2-L2); `WiringCheck`'s summary matches the README (S5-M1); `RecordHarpDevices` with no `PathPrefix` before it stops the workflow at Start with its note's text (7 Nov's L2-L2); a port is plugged in when its WMI `Status` is `OK`, as rig-setup's `present`, its name from the registry's `PortName` first (S4-L5); `HarpRig.MapToWrite` gives no register map for a kind with no known WhoAmI, so `<Name>/<Name>.yml` is never written to meet the folder entry (S3-I1). A test for each but the port reading, which needs WMI.
- Shared: `RigDevices.Value` reads `null`, `Null`, `NULL` and `~` as empty (S4-L1), tested in HarpDevices.Tests.
- LiveView: `LiveViewReport.Streamed`'s summary names a board's two streams (L2-I1); the rate note says "above 0 and at most 1000" (7 Nov's L2-I2).
- open-rigs `behaviour_box.py`: the wiring `device` description says nothing holds the word to a lab list yet (S5-M1).

Still open: the wiring words check (set B), app-rig-setup's null comment (the night's set C), and `RecordHarpDevice.cs`'s "when its port opened" wording (7 Nov morning, under Not changed), which was not in this round.

## Audit 2026-11-08, morning (/audit-rigs, set C, run 4: the seams, scripts and packaging)

Changed (scripts and COEN-LAB.md only; no package source, so no version raised):

- The comment-only filter passed every change (L3-H1, reproduced): git hands `-I` each line with its newline and lets `^` match after it, so `'^\s*$'` matched the empty end of every line, and the push's "changed since built" and "built on other source" checks have passed any source change since ce786bc (6 Nov). `-I` and `--ignore-blank-lines` also each ignore only a hunk wholly of their own kind, so a comment added with a blank line still counted. `$script:CommentOnly` is now one pattern ending in the line's own newline (`^[ \t\r]*(//[^\n]*)?\n`), checked across nine cases (code, mid-line code, trailing comment, commented-out code, blank, blank and comment, `///`), and `Test-CommentOnlyFilter` holds it against two known commits at every run. Every package passes the stricter checks at HEAD, so nothing stale or misbuilt shipped.
- `update-workflows.ps1` run alone shipped whatever `local_packages\` held, unchecked (L3-M1): the push's package checks are `Assert-LabPackagesShippable` in `lab-packages.ps1`, run by both.
- The "main moved while this run waited" `exit 1` kept the run lock in an in-process run (L3-L1, set C's L3-L2 through `exit`): it lets the lock go first.

Not changed, do not raise again without new information:

- `update-workflows.ps1`'s clone cleanup deletes any unlisted folder under `-Work` (L3-I1): nobody passes `-Work` but the script's own re-run, and `Set-Branch` already resets listed folders there.

## Audit 2026-11-08, early morning (/audit-rigs, set B: the seams, HarpDevices, LiveView and Shared)

Changed (docs and tests only; no package source, so no version raised):

- The HarpDevices README said rig-control's names check holds each wiring `device` word to the lab's list (S5-M1): nothing reads `wiring`, `board-lines.yml` or `wiredDevices`, and `K:\config\device-dictionary.json` has no `wiredDevices`. It says the check is planned and not there yet. No workflow has a `schemas\board-lines.yml` yet, so no wiring goes unchecked today.
- The Manifest README's items 2 and 3 under When It Writes were run together by e0740ec (S3-L3).
- `workflows.txt`'s header said its list is workflows not on rig-setup, beside coen-qc-calibrate-valve, which is (S5-L1): it says the list is besides rig-setup's and may overlap it (the duplicate stays, 6 Nov decision).
- HarpDevices.Tests' "waits until the dump has finished" read a list the timer thread writes, with a 100 ms quiet against a stall (L2-L3): a 500 ms quiet for that block, the list read under a lock.

Not changed, do not raise again without new information:

- The board channel names in `WiringCheck.cs` and `behaviour_box.py`'s `BoardWiring` (L2-I2): they agree today, and drift fails loudly ("not a line of the board") on fixed hardware.

Done since (8 Nov 2026, Pip: "Land all of it"): the wiring words check (S5-M1, DECISIONS.md 7 Nov). `wiredDevices` is in the device dictionary (RewardValve, Photodiode, CameraTrigger, AirPuff, ZapitTrigger, AudioDevice, TimestampGeneratorGen3), rig-setup's Save Names keeps it as a `Wired:` line (4b81048), and rig-control's `check-workflow-names.ps1` reads the workflow's `schemas\board-lines.yml` (55feaf1).

## Audit 2026-11-08, small hours (/audit-rigs, set A: the seams, Control and Manifest)

Changed:

- **Manifest 0.1.1-coen-lab.11** (coen-lab.10 was built and never pushed; see L1-L2 below), every Manifest item queued for its next change in one raise: the run's error after the workflow's Finished or the controller's stop is recorded as `completed` or `stopped`, not `error` (L1-L1); `FilesSection` skips every `.tmp` and drops a file deleted while it is listed, and `stopWritten` is set only after the write succeeds (set C's S3-L1); the dead `Failed` branch and `ExperimentManifestException` are gone (7 Nov's L1-I1); `ConfigFiles` defaults from `RigDevices.DefaultConfigRoot` (7 Nov's L2-L1). Tests for each.
- DECISIONS.md: a dated entry that coen-qc-calibrate-valve's `ValveInterleave.cs` (since 80e2fe7) orders bursts and fits nothing, so the 6 and 7 Nov "no C#" wording is out of date and the rule holds (S5-M1).
- COEN-LAB.md: the Manifest row names the optional run input (S5-I1); the Shared row says Control compiles `WorkflowFile.cs` alone (L1-I2); Manifest 11.

## Audit 2026-11-08, night (/audit-rigs, set C: the seams, scripts and packaging)

Changed (scripts and docs only; no package source, so no version raised):

- `update-workflows.ps1`: its self-update re-run passed `-Extra ''`, which Windows PowerShell drops, so the child took `-Reference` as `-Extra` and stopped (L3-L1, probed); a throw between taking the run lock and its `try` left the lock held for an in-process run (the trap releases it now, the `finally` clears `$mine`; probed: `finally` runs before the trap) (L3-L2); clones of a repository no longer listed are removed at the end of a run (coen-calibrate-valve's 337 MB) (L3-I3).
- `push-and-update-workflows.ps1` refuses uncommitted changes in `local_packages\` (L3-L3): a build not committed passed every check (on disk, no commit for git to compare), so the raised `<Version>` went up without its `.nupkg`.
- COEN-LAB.md: the blue opto rigs' rig.yml is in av-dome's keyed layout since 7 Nov with no `rigId` yet (S5-L3); the pin excepts READMEs and test projects, as the script does (S1-I1). The HarpDevices README says rig-setup scans half-hourly (S5-I1).

Not changed, do not raise again without new information:

- `Get-LabPackages` counts every change under `src\Shared` as Control's though Control compiles only `WorkflowFile.cs` (L3-I1): it errs towards a raise, and a narrower pathspec per Compile item is one more thing to keep right.
- The push does not re-run `check-package-dependencies.ps1` over `local_packages\` (L3-I2): Bonsai installs exactly the versions `Bonsai.config` lists, so a nuspec dependency is metadata; today all four Core dependants name Core's version.

Open, with the next change of each package:

- app-rig-setup's `rig-config.ps1` comment that the readers agree on YAML's null (S4-L1's other half; `RigDevices.Value` reads `Null` and `NULL` since 8 Nov): a app-rig-setup change, not this repository's.

## Independent audit of 7 Nov work, 8 Nov 2026 (the wiring check and the board channels)

Changed:

- HarpDevices 0.1.1-coen-lab.7: `zapit.triggerLine` counts only while `zapit.hardwareTriggered` is true, so an honest wiring on a rig whose Zapit TTL is not cabled (GREEN-DOME-1, BLUE-OPTO-1) no longer refuses; the refusal is one line, its disagreements joined by `; `, as rig-control keeps only a workflow error's first line; two entries for one line need every target either names, where only the first's were checked. The README's `schemas\board-lines.yml` had a backspace for `\b`.
- `workflows.txt` lists `ucl-open/coen-qc-preflight-rig`, which was on HarpDevices coen-lab.5 the day .6 went out because neither list had it.

Not changed, do not raise again without new information:

- `WiringCheck` compares `device` and `side` case-sensitively and reads `wiring` keys by their exact channel names, so `side: Left` or `do1:` refuses or reads as unknown: the rig schema (`Side`, the channel aliases) already rejects both before launch.

## Audit 2026-11-07, morning (/audit-rigs, set B: the seams, HarpDevices, LiveView and Shared)

Changed (scripts and docs only; no package source, so no version raised):

- `update-workflows.ps1` exited 0 when main had moved while it waited, and ended "every lab Bonsai workflow is on the lab's packages" with superseded branches left for "the run on that commit", which happens only if somebody runs it (S1-M1; a plain `git push` runs nothing, and 86daefc's file stayed stale a day): both exit 1 and say to run `push-and-update-workflows.ps1` again from the newer commit.
- The Manifest README said the manifest's `control` section holds every parameter at Go (S5-M1): they are in `Settings\Settings_Experiment_<DateTime>.json` since 0.1.1-coen.5.

Not changed, do not raise again without new information:

- `RecordHarpDevice.cs` keeps writing every reply into `atConnection` after Start, so a timed-out dump's fallback may be the previous experiment's registers under "when its port opened" (L2-I1): the same board, the same registers; wording, with the next HarpDevices change.
- DECISIONS.md's stage-2 line lists `rollouts.json`, which the share has not got yet (S5-I1): decisions are append-only, and the file appears at the first rollout.

## Audit 2026-11-07, early hours (/audit-rigs, set A: the seams, Control and Manifest)

Changed (scripts and docs only; no package source, so no version raised):

- `check-workflow-builds.ps1`'s names step assumed the `orcl:` prefix (any other read as "ok 0"), pooled commands and parameters into one list, and missed `ParameterValue`, `ParameterDefault` and `ParameterMaximum`, whose wrong name throws only at Go after the reply said ok (L1-L1): the prefix is read from each file's xmlns (a file with the package's nodes and none fails), the two lists are kept apart, and the five nodes are checked. Probed: the four lab workflows pass; a misspelt Snapshot under another prefix fails.
- `FORMAT.md`'s example named `0.1.1-coen.1` with prefix entries it never had and no `trial_columns`; `config_files` dated 7 Nov for a 6 Nov commit; a note that after an `XXX` rename `session` is the authority and `control`, `launch` and the settings file keep what was typed (L1-L3, S3-I1). The Control README has a `GoByName` row and the right date for ExperimentSettings (L1-L4, S5-L); COEN-LAB.md names the current versions and the build check's four steps (S5-M2, S5-L1); DECISIONS.md notes `Notes` left the launch on 6 Nov (S5-L); the Manifest README's `RigSchema/x.json` is dated history (S5-I).

Not changed, do not raise again without new information:

- The comment-only filter (`lab-packages.ps1`) ignores `///` doc comments too, so a summary edit ships at the next behaviour change (L1-L2): the IL is the same, and a raise per summary edit redeploys every workflow.

Open, with the next change of each package:

- Core README: no mention of `DescribeCalibrationCurve` (S2-L3); with the `ApplyBalancePort` removal below.

## Audit 2026-11-06, evening (/audit-rigs, set C: the seams, windows, help and live view, tests, trust, scripts and packaging)

Changed:

- **`push-and-update-workflows.ps1` refused every push after `86daefc`** (S1-M1, L3-M1, reproduced): that commit changed only comments in two Manifest `.cs` files and was pushed with plain `git push`, so the stale check read Manifest as changed since built, and the share's `lab-package-versions.json` stayed at `9702b51`. The stale and misbuilt checks now ignore a change whose lines are all comments or blank (`git diff -I`), through `scripts\lab-packages.ps1`, which `build-package.ps1` shares.
- **`build-package.ps1` packed uncommitted source while the nuspec named HEAD** (L3-M2, probed on a scratch clone: a package matching no commit passed every push gate): it refuses when the package's inputs have uncommitted changes, so the source is committed first (COEN-LAB.md step 3).
- An older run of `update-workflows.ps1`, after waiting for the lock, wrote its versions over a newer run's `lab-package-versions.json` (L3-L2): it checks origin's main again before writing and stops as superseded.
- `check-workflow-builds.ps1` passed a workflow listening for a command or parameter ExperimentControl does not declare (L3-L4, probed: a misspelt Snapshot passed all three steps): the names are compared, in every `src\**\*.bonsai`, and a stray one fails the check. Every lab workflow passes today.
- `patches\README.md` named `0.1.1-coen.N` versions and hashes no file has (L3-L5); the HarpDevices README's `_Hardware.json` path (S5).
- `build\` is among every package's stale inputs (L3-L3; Pip, 7 Nov 2026: rebuild all nine now): `86daefc`'s `Common.csproj.targets` change had flagged nothing, so every lab package was raised one and rebuilt in one go, which also gave Logging, Streaming and Video the right dependency versions.

Not changed, do not raise again without new information:
- A `build-package.ps1` run during `update-workflows.ps1` in the same clone removes the `.nupkg` the run recorded (L3-L3): it fails loudly and the next run repairs it.
- coen-qc-calibrate-valve listed in `workflows.txt` and `profile-repos.json` both (L3-I1): it is updated even if removed from one.
- A `trial-columns.yml` with two keys differing only in case would hold its session every night (S3-L1): no file has one, and it is an alarm naming the session, not a silent hold.

Open:

- lib-open-rigs: remove `ApplyBalancePort` and `BalancePort` (OpenRigs.Core `ManualBalance.cs`, their tests, and the mentions in `ManualBalance.bonsai` and `docs/articles/calibration.md`), unused since coen-qc-calibrate-valve went manual-only (Pip, 6 Nov 2026; moved here from rig-control's BACKLOG).
- coen-qc-calibrate-valve has no `config\main.layout` in git, so the build check's layout step is skipped (L3-I2): to be tracked in that repository.

## Audit 2026-11-06, late afternoon (the Settings rename and the coen-lab versions)

Changed: each package's lab dependencies now carry the depending package's own `.csproj` version (`build\Common.csproj.targets` keeps a `-coen-lab.` `<Version>`, `build-package.ps1` no longer passes `-p:Version` and refuses a mismatch through `scripts\check-package-dependencies.ps1`). The packages built today keep their wrong dependencies (Logging `coen-lab.4` names Core `coen-lab.4`) until each one next changes; Bonsai installs what `Bonsai.config` lists, so no workflow is affected (M2). The `Schemas\` layout of 6 Nov 2026 is named, with its commits, in every history line that skipped it, and the dates are corrected from 7 Nov (M1). The current rule in rig-setup `docs\manifests.md` now uses the new names (L1). The retroactive mapping now lists the settings files as paths (L2). "Left" now names `Schemas_*` in the dictionary (L3).

Not changed, do not raise again without new information:

- Test data in rig-setup and rig-control still uses `0.1.1-coen.N` strings: the version checks compare strings, so the label does not matter.
- `K:\config\data-dictionary.json` edited directly rather than through rig-setup's Repos page: a backup was made and a history entry written, so the result is the same.
- A rig using its cached copy of the names while K: is down: the launch is blocked and the message names the missing name, so it fails loudly.
- coen-behaviour-box writes no `Settings_Task`: it has no task file, and `specs/logging.md` says file names are not strict.
- Lab versions rank below upstream `0.1.1` (checked with NuGet.Versioning: `0.1.1-coen-lab.8` < `0.1.1`): intended, since the packages keep upstream ids.
- N carried on under the new label rather than restarted: every `coen-lab` version ranks above every `coen` one (NuGet.Versioning: `coen-lab.2` > `coen.9`), and carrying on means no number is reused.
- Control `coen.6` and Manifest `coen.7` were current for only about 20 minutes: each version is unique, so nothing could be cached wrongly.
- Old package folders left in a workflow's `.bonsai\Packages`: Bonsai loads only the `AssemblyLocation`s in `Bonsai.config`.

## Audit 2026-11-06 (independent audit of stage 4: one updater, the hourly task retired)

Changed: `lab-package-versions.json`'s Replace is tried five times a second apart, since it fails while a rig is reading the file (M1, probed on the share: Replace works, and fails with a reader open); update-workflows runs only from `main` here (L3); a branch is not pushed, as superseded, when lib-open-rigs' `main` has moved since the run began, so a run on another machine cannot take a workflow back to older versions (L2). rig-setup and rig-control items are in their own BACKLOGs.

Not changed, do not raise again without new information:

- rig-control not knowing Leave Main Alone: its warning comes only after the names check passes, on a project's main workflow, and the domes run av-dome `main` from its Bonsai shortcut, not through rig-control.
- The share file written at the start of a run: deliberate, so a run that stops part way shows as workflows behind; a clean run clears within one 30-minute pull.
- The `trap` in update-workflows: it still gives whoever pushes a "Stopped:" reason.
- rig-control silent when the share or the file is missing: warn-only was the decision, and rig-setup's rows cover the share.

## Settings files written by rig-control, as open-rigs intends (plan, Pip, 5 Nov 2026)

open-rigs keeps parameters in files written before Bonsai and checked against their schemas (`specs/logging.md` section 6; `RigPath`, `SessionPath`, `TaskPath` in the reaction-time tutorial). The lab's rigs will do the same, with rig-control writing the task file. Built 6 Nov 2026 (Pip: "Fully implement", the whole plan), in three stages. Stage 1: the three settings files in `Schemas\` (`Schemas_Rig`, `Schemas_Task`, `Schemas_Experiment`; `Settings\` and `Settings_*` from coen-behaviour-box `1a8f95d` and av-dome `72d7d39`, later on 6 Nov 2026, Control 0.1.1-coen.6, Manifest 0.1.1-coen.7), each expected on its own, and the experiment file from `ExperimentSettings` (Control 0.1.1-coen.4, Manifest 0.1.1-coen.5; coen-behaviour-box `dc212a6`, av-dome `pip/main-switchover` `4c95bae`, rig-setup `d7dd32f`, rig-control `e9663a9`, `K:\config\data-dictionary.json` streams). Stage 2: rig-control writes the task file at Go (rig-control `ad982a6`, av-dome `5635911`; `ManualRewardUl`, Pip's choice, sets manual rewards before Go, in place of `RewardSizeUl`'s second job). Stage 3: `check-workflow-builds.ps1` step 3 (`scripts\check-config-schemas.py`), a task file's `...ConfigPath` files kept beside it (rig-control `68ddb79`), calibration files and the mouse's spout positions hashed (Manifest 0.1.1-coen.6, av-dome `1f3d7b3`). Where the build differs from the items below: a list of rows (trainingStages) is entered row by row (`trainingStages.4.rewardSizeUl`), only a list of values is edited as text; a workflow started outside rig-control shows no task fields rather than read-only ones (rig-control does not know its task file); every schema check closes each object to keys the schema does not list, as `DeserializeFromYaml` does, since pydantic's schemas let a misspelt key through. Left: `Schemas_Rig`, `Schemas_Task` and `Schemas_Experiment` come out of `K:\config\data-dictionary.json`'s `streams` once both domes run av-dome `72d7d39` or later and GEEK-BBOX1 runs coen-behaviour-box `1a8f95d` or later (`RigSchema`, `TaskSchema` and the old experiment settings came out on 6 Nov 2026, the dictionary's history says); and the retroactive renaming below. Words (Pip, 5 Nov 2026): an experiment is one workflow run; a session is the mouse's sitting, which can hold several. Each experiment has its own folder, `sub-<mouse>\ses-<SessionId>_<DateTime>`, named for the session it belongs to.

- **Three settings files, all in the experiment folder's `Settings\`**: `Settings_Rig_<DateTime>.json` (rig.yml as loaded), `Settings_Task_<DateTime>.json` (the task as Bonsai deserialised it) and `Settings_Experiment_<DateTime>.json` (open-rigs' `ExperimentSession` fields: subject, session, workflow, commit, repository URL; plus the rig, who launched, who pressed Go and every ExperimentControl parameter at Go). Written by `LogDataSchema` named `Settings_Rig`, `Settings_Task`, `Settings_Experiment` (a log's folder is its name up to the first `_`). Replaces `RigSchema\` and `TaskSchema\`, which are named by `ExpectedFiles`, the two `check-workflow.ps1` lists, `K:\config\data-dictionary.json`'s `streams` (rig-control's names check refuses `Settings_*` until they are listed there) and descriptions (av-dome's `ExperimentManifest`, `task.yml`'s header, `FORMAT.md`'s example).
- **Each settings file expected on its own** (audit M2): one `Settings/` entry is met by any one file, so `ExpectedFiles` and rig-sync take a prefix entry (`Settings/Settings_Task_`) that a file whose path starts with it meets, and workflows list all three.
- **The workflow writes the experiment file, in every case** (audit H1): a node in OpenRigs.Control writes it from `ControlState` at Go, which holds the exact parameter values (including those set by `ParameterDefault`) and who launched and pressed Go; the commit, repository and workflow come as the manifest reads them. No `ExperimentPath`, no second writer, and rig-control needs neither `open_rigs` nor pydantic. The manifest's `control` section keeps who and when, and the parameters move to the experiment file.
- **rig-control writes the task file at Go**, before relaying Go: the chosen task file with the form's edits, and the RNG seed drawn there when the file says `rngSeed: null`; validated against the project's `src\DataSchemas\*_task.json` (`jsonschema`, a new rig-control dependency), Go refused naming the field if it fails. Written to `C:\Coen-Lab-Local\rig-control\` (outside the project, so the manifest's `dirty` stays false and the rig's pull has nothing to conflict with); its path passed at launch as `--property:TaskPath`, only for a workflow exposing it, so rigs move over one at a time, and added to rig-control's `LAUNCH_OWN` so the form never offers it. `TaskFile` becomes the form's choice of task file at launch; `TrainingStage` stays an ExperimentControl parameter selecting a row, so the experiment file says which stage ran (audit M3).
- **The form** (audit M3, M4): every task field editable; each project's `config\rig-control.toml` ticks the ones shown by default. Lists (av-dome's `trainingStages`, `speakerLocations`) edit as text; the ticked single values are built first. For a workflow started outside rig-control, whose `TaskPath` rig-control did not choose, the task fields are read-only.
- **Without rig-control** (the editor by hand) `TaskPath` defaults to `config\task.yml`, and the experiment file is written as always.
- **The `RngSeed` event stays** (audit H2): with `rngSeed: null` in the file (an editor run) `InitialiseTrial` draws a seed nobody else records, and the logging spec (section 7.2) needs it recoverable.
- **Files read by path are kept or hashed** (audit M1): the Zapit conditions file the task names (`zapitConfigPath`, today an absolute machine path in `task.yml`) is copied beside the task file; rig.yml's calibration files (`meshMapPath`, `gammaLutPath`, `calibration\AuditoryStimuli`) and the mouse's spout positions (`calibration\LickSpoutPositions\<SubjectId>.csv`) go in `ExperimentManifest`'s `ConfigFiles` so their hashes are recorded.
- **av-dome**: `ApplyTaskOverrides` and its four parameters (`RewardSizeUl`, `ItiMin`, `ItiMax`, `Zapit`) go. Live parameters (`StepSize`; coen-behaviour-box's `ExposureTime`, `Gain`) stay ExperimentControl parameters; coen-behaviour-box's `RunLengthMins` stays one until it has a task schema.
- **Task files checked at commit** (audit M5): check-workflow-builds validates each `config\*.yml` against its own schema: `task*.yml` against `*_task.json`, `rig-template.yml` against the rig schema, `valve-calibration.yml` against `calibration.json`. Today nothing checks them before Bonsai reads them at Go, where the generated `DeserializeFromYaml` (no `IgnoreUnmatchedProperties`) throws on an unknown or misspelt key and the experiment ends early.
- **Retroactive renaming of recorded experiments, later** (Pip will rename a lot): one old to new mapping drives it (trial columns by each workflow's `formerly` in `schemas/trial-columns.yml`; settings files by path, each a move and a file rename, not a folder rename: `RigSchema\RigSchema_<DateTime>.json` to `Settings\Settings_Rig_<DateTime>.json` and `TaskSchema\TaskSchema_<DateTime>.json` to `Settings\Settings_Task_<DateTime>.json` (before coen-behaviour-box `dc212a6` and av-dome `4c95bae`), and `Schemas\Schemas_Rig_`, `Schemas_Task_`, `Schemas_Experiment_` to `Settings\Settings_Rig_`, `Settings_Task_`, `Settings_Experiment_` (6 Nov 2026 only, both domes and GEEK-BBOX1, up to coen-behaviour-box `1a8f95d` and av-dome `72d7d39`; renamed at Tom Ryan's point that they are settings, not schemas), with each manifest's `expected` paths (`Schemas/Schemas_Rig_` and the rest) rewritten too, logs such as `TrialSettings` to `BonsaiData_Trial`, in a list beside it), on the server copy (RDSS: renames only, never copy), rewriting each experiment's `manifest.json` file list in the same step; column renames rewrite CSV header lines, so backup and `--dry-run` before `--apply`. Inside an experiment folder is free; the `sub-<mouse>\ses-<id>_<DateTime>` levels and the server's `<RIG>/sub/ses` are hard-coded in about seven tools (ManifestWriter, LiveView's `SessionKey`, LoggedRows, rig-sync, app-rig-control, app-rig-setup's `rootPath` rule, ingest).

## Audit 2026-11-05, evening (settings-files plan and the Session -> Experiment rename): not changed

- rig-control accepting `SessionStoppedException` as well: only a workflow on packages before 0.1.1-coen.2 prints it, rig-setup deploys within 30 minutes, and an answered stop is classified `stopped` either way.
- The third file's property named against open-rigs' `SessionPath`: moot, as the workflow writes the experiment file and no path is passed.
- Extending `ApplyTaskOverrides` instead of rig-control writing the task file: Pip chose the long-term route.
- The retroactive renaming's details (the seven tools, rig-sync's two-level rename detection): backlogged by Pip, not checked.

## Approved names, 5 Nov 2026

- **A package's dependency on Core names its own version, not Core's.** `build-package.ps1` passes `-p:Version` globally (`build\Common.csproj.targets` sets `<Version>` after each project's), so it reaches the referenced Core: Logging `0.1.1-coen.2` asks for Core `>= 0.1.1-coen.2`, which does not exist. Harmless while Bonsai restores only what `Bonsai.config` lists (Logging `bbox.6` asked for Core `bbox.6` the same way), wrong for anything that resolves dependencies. Fix: a lab `<LabVersion>` in each `.csproj` that `Common.csproj.targets` takes over its default, and no global `-p:Version`.
- **Experiments recorded before av-dome took the new column names** (`TrialCnt`, `correctResponse`, `response`, `targetSide`, `BothSpouts`) no longer map in its `rig-control.toml`, which names only the new ones; `older_logs` covers `TrialSettings` only. Test experiments so far.
- **Async event names** (`RewardDelivered`, `RngSeed`, the controller's) are not in the lab's data list (`K:\config\data-dictionary.json`) yet.
- **Names are checked on the rig only, since 6 Nov 2026** (DECISIONS.md stage 2): app-rig-control's `check-workflow-names.ps1` at deploy and before each launch, against `K:\config`'s lists. An author learns of a name off the list when a rig refuses the version, not before pushing; running that script by hand on a lab machine checks first. `src\OpenRigs.Logging\CsvWriter.cs` (~112) still says `ColumnNames` is for check-bonsai: it is for app-rig-control's check now; reword it with the next Logging version raise, since any source change needs one.

## Test package changes on one rig first (Pip, 5 Nov 2026)

- A change pushed here reaches every lab workflow's ticked branches and `main` at once, gated only by each workflow's build check, which passes a change in behaviour. Proposed: a `dev` branch here whose `push-and-update-workflows.ps1` updates only targets marked in `profile-repos.json` (e.g. `"labPackages": "dev"` on one workflow branch, later a app-rig-setup tick), each target on exactly one channel so the next `main` run does not revert it; promotion fast-forwards `main` to `dev` and runs `push-and-update-workflows.ps1`. Every dev build gets a new version, since Bonsai caches by version, and `push-and-update-workflows.ps1` checks that promotion ships only versions that were on dev. Check first whether app-rig-setup deploys a ticked branch on one machine or every rig of the profile.

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

- `RequestRegisterDump` reading WhoAmI itself about 1 s after Start, so a wrong-kind board stops in ready mode when the connection dump was missed (L2-L1): `CheckBoard` already confirms the USB serial at Start, so a wrong kind is a rig.yml filing a right serial under the wrong kind, caught on the first experiment with the record written first.
- `StoppedBy` before `StopReason` by store order, no lock or volatile (S2-I1): x64 never reorders stores, and every rig is x64.
- Done (HarpDevices local.9, LiveView local.14): the packed READMEs of HarpDevices local.8 and LiveView local.12 predated a1b9984's `licketySplit` and ConfigRoot notes (L2-I1).
- OpenRigs.Core's and the template's `C:\RigConfigs` default (S4-I2): upstream's, and both lab workflows set `ConfigRoot`.
- coen-av-arena takes `ucl-open-rigs` from upstream main unpinned while its row reads up to date (S1-I1): an old-template workflow with no OpenRigs node, nothing to keep in step.

Environment: the UCL-Open coen-behaviour-box clone on PIPOFFICERIG holds Control local.13 and no Logging bbox.4 while its Bonsai.config (f6983b3) names local.14, so rig-control's `test_bonsai.py` skips there (S2-I2); `.bonsai\Setup.ps1` in that clone brings it up, for the rig-control session.

## Audit 2026-11-04, evening (/audit-rigs, set A; rig-control left to its own session)

Changed (Manifest 0.1.0-local.15, Control 0.1.0-local.14):

- **A run that failed before the experiment folder existed had its error held back** (L1-M1, reproduced): `ManifestRecorder` kept it until the folder appeared, so a workflow whose only subscriber to the run was `ExperimentManifest` stood at Ready with a dead run, and a Go then made a folder for it. The error now ends the node at once (no folder, no experiment to record); a completion before the folder still waits for it. Tested.
- `ControlServer` sets `StoppedBy` before `StopReason` (L1-L1): the Manifest's stop poll fires on `StopReason` and then reads `StoppedBy`, so the other order could write the running stop without the person. Not reproduced; the window is two assignments.
- `scripts\update-workflows.ps1` says in the row when a repo with `src\DataSchemas` has no `regenerate-schemas` script, so its schemas stay stale at every pin move (S4-M1: av-dome's `pip/main-switchover`, whose `examples\rig.py` would also fail the step 7e9ed6f added; both for that branch, see Deferred).
- Docs: the Manifest README says `BonsaiData/` is the `ExpectedFiles` entry for `LogTrial` and `LogAsync` (L1-L2, S3-L2: the files carry the hour and wildcards are refused; an experiment with no trials still meets it through `BonsaiData_Async`); FORMAT.md says `closed: false` also comes from Bonsai's own writers, `BonsaiData_Async` open until disposal among them (L1-I); COEN-LAB.md names the four packages and the Logging and Core test projects (S5-L3).

Not changed, do not raise again without new information:

- `LogAsync` does not write the `Go` row rig-control's water count starts from (S2-H1): av-dome mirrors `ControlEvents` into `AsyncEvents`, so the row is there, and Logging stays independent of Control. The rule (a workflow with `LogAsync` sends its `ControlEvents` through `CreateAsyncEvent`, else rig-control records no water) belongs in claude-tools' bonsai-workflows skill and rig-control's README, not the package.
- Parameter limits not in `status` (S2-I1): a refused `set` names them; a second copy would drift.

Deferred, for Pip:

- Done on Pip's word (5 Nov 2026), in Logging 0.1.0-bbox.5 and LiveView 0.1.0-local.13 (CsvWriter's `AutoFlush` and `PublishRows`, `LoggedRows`, the `logs` stream): **`BonsaiData_Trial` and `BonsaiData_Async` reach disk about 4 KB at a time** (S3-M2): `OpenRigs.Logging.CsvWriter` rewraps Bonsai's own `CsvWriter`, which never flushes. The session page runs 15-25 trials behind, and a run killed past its stop deadline loses its unflushed tail, so `async_water_ml` undercounts with nothing saying so. A flush per row on `LogTrial`/`LogAsync` only (a few rows a second) is a patch to the bbox Logging package; a Harp log at full rate must not have it.
- A `Detail` starting with `"` swallows every later row of `BonsaiData_Async` in app-rig-control's `csv.reader` (S3-L3): `AsyncEvent.Clean` replaces commas and line breaks, not quotes; no sender today starts one. `Clean` could replace `"` with `'`, and app-rig-control read with `QUOTE_NONE`.
- av-dome `pip/main-switchover` (S4-M1): add `regenerate-schemas = "ucl_open_av_dome.regenerate:main"` to `[project.scripts]`, and make `examples\rig.py` write `config\rig-template.yml` (today it writes `.\local\UclOpenAvDomeRig.json` and calls `UclOpenAvDomeRig()` with no arguments, which throws) or delete it; with the script line alone, every update-workflows run would read FAILED.

## Audit 2026-11-04, afternoon (/audit-rigs, set C)

Changed (scripts and READMEs only, no package version raised):

- `scripts\hourly.ps1` writes the share's `<MACHINE>_WorkflowsFailed.txt` as UTF-8 without BOM from the log's text: the log is UTF-16 (`*>` under PowerShell 5.1), and the copied bytes reached ClaudioVisual as a NUL after every character. Probed: the log starts `FF FE`, the share copy `53 74`, no NUL bytes.
- `hourly.ps1` stops with `Stopped: fetch failed.` and `Exit 1` when the fetch of its own clone fails, as its other stops do; unchecked, the run went on with last hour's origin/main.
- `scripts\schedule.ps1` registers `%LOCALAPPDATA%\lib-open-rigs\self\scripts\hourly.ps1`, cloning `self` if missing, so the task runs what origin/main has, not the working copy's checkout or unpushed commits. hourly.ps1 resets that clone, its own file included, which is safe as PowerShell parses a `-File` script whole first. PIPOFFICERIG's task keeps the old path until `schedule.ps1` is run there again.
- `scripts\push-and-update-workflows.ps1` refuses, before pushing: uncommitted changes under `scripts\` (the parse check reads the files, the push sends HEAD); after `git fetch`, a `.nupkg` that a commit in `@{u}..HEAD` adds or changes while origin's history already holds that path; a lab `.nupkg` whose nuspec commit this clone lacks, or whose source there differs from the commit it was added onto (its parent), the 6d0b870 rebase case; and two versions of one package in `local_packages\`. `Directory.Build.props` and `.targets` now count as every lab package's source. Probed in a clone without a remote: today's tree passes, and each refusal fires on a recreated case.
- `scripts\update-workflows.ps1` stops a workflow's update when `dotnet tool restore` fails, naming it, instead of failing later in `regenerate-schemas` with no reason.
- Docs: the HarpDevices README lists `licketySplit` 1400; the HarpDevices and LiveView READMEs say only `check-workflow.ps1` maps `ConfigRoot` (to a temporary root) and rig-control refuses any other (rig-control 482dd5d); the LiveView README says the stream's `rigId` is the machine name; COEN-LAB.md and `hourly.ps1` say ClaudioVisual DMs the same failures and a `finished` over 26 hours old, and COEN-LAB.md lists push-and-update-workflows.ps1's refusals and the task's clone.

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
- `RecordHarpDevice` passes an error on the board's messages to the workflow (`observer.OnError`); before the experiment folder appeared it was dropped. Tested.
- A board whose WhoAmI came only after the experiment started has the note "The board reported no WhoAmI at connection, so its kind was checked only after the experiment started." in its record: a wrong kind then stops the workflow after Go, not before. Tested both ways.
- HarpDevices' dump-request test polls for the read for up to 2 s instead of a fixed 100 ms sleep.
- `scripts\hourly.ps1` runs `update-workflows.ps1` as a child process, as `push-and-update-workflows.ps1` does: called in-process, a script that did not parse left `$LASTEXITCODE` at git's 0 and the run was recorded as a pass (probed: a missing `}` gave `Exit 0`). The child gives `Exit 1` and the parser's error in the same log. No parse check of `scripts\*.ps1` was added to CI: `.github\workflows\OpenRigs.yml` is upstream's, not in COEN-LAB.md's table, and the lab's scripts are tested nowhere else.
- A controller's stop is written as soon as it comes, outcome still `running`, with `run.stop_reason` and `control.stopped_by` (`ManifestRecorder` looks for it every 0.25 s; `ManifestWriter.Stopping`, the same atomic write): rig-control kills a workflow slow to stop, and the end-of-run write that carried them never came. Tested; FORMAT.md says a `running` manifest may carry the stop, and its example writer is local.14.
- Docs: COEN-LAB.md and `hourly.ps1` name every reader of `<MACHINE>_WorkflowsUpdate.json` (rig-setup's rig-status report and Machines window at 3 h, ClaudioVisual's rig health DM at a day); the HarpDevices, LiveView and Manifest READMEs say what changed.

Not changed, do not raise again without new information:

- `src\Shared\WorkflowFile.cs`'s empty catches in the git readers (`:90`, `:141`, `:173`): reading .git must never stop an experiment, and the summaries say null when it cannot be read.

## Audit 2026-11-04, morning (/audit-rigs, set A)

Changed (Control 0.1.0-local.13, Manifest 0.1.0-local.12, HarpDevices 0.1.0-local.6, LiveView 0.1.0-local.10, the last two for `src\Shared`; pushed with e80399b):

- `AtomicFile.Write` (`src\Shared\Json.cs`) tries the rename up to 10 times, 50 ms apart: Windows refuses `File.Replace` while a reader holds the file without delete sharing (Python's `open()`; rig-control's agent polls `manifest.json` every second around the final write), and the final manifest was then lost. A Manifest test holds the file open for 200 ms during a write (fails without the retry). A final write that fails goes to stderr as well as the trace (`ManifestRecorder`).
- `RunEnd.AtDisposal` says `stopped` only when the controller's stop set `StopReason`, as FORMAT.md says, not for any Stopping phase; `ControlExperiment.Stopping` is gone. Tested.
- `ExpectedFiles` drops a leading `./` (rig-sync matches `RigSchema/x.json`) and leaves out, with a note, an entry that is rooted or holds `..`, as it does a wildcard. Tested.
- `ControlServer` reads every frame of a request and refuses more than one ("one frame per request, got N"): a second frame left unread made the reply throw, which ended the experiment. Tested with a two-frame status (fails without the change).
- A Control test that Go and a command are answered within 1 s while a subscriber spends 3 s on each. The two-servers test waits for the first to answer before starting the second (they raced to bind).
- `scripts\hourly.ps1` writes `K:\config\<MACHINE>\<MACHINE>_WorkflowsUpdate.json` every run (`machine`, `started`, `finished`, `exitCode`, `lastPass`; atomic, UTF-8 without BOM; a failed share write is logged, not fatal), which rig-setup's rig-status report flags when `finished` is over 3 h old or `exitCode` is not 0: nothing noticed a task that stopped running. COEN-LAB.md says so, and no longer says rig-setup's Machines window shows `WorkflowsFailed.txt` (PIPOFFICERIG has no heartbeat, so it never did; set B above says it did).
- Docs: FORMAT.md's example writer is Manifest local.12 and `expected[].path` has no `./`; the Control and Manifest READMEs say what changed.

Not changed, do not raise again without new information: none.

## Audit 2026-11-04 (/audit-rigs, set C)

Changed (LiveView 0.1.0-local.9, Manifest 0.1.0-local.11; Pip: "Implement everything recommended as you go"):

- `push-and-update-workflows.ps1` refuses a package whose source (its folder, its READMEs aside, and `..\Shared\` it compiles) changed after its `.nupkg` was committed with `<Version>` unraised: it pushed and shipped nothing. It found Manifest local.10 built before 7b02057's Shared change, so Manifest is local.11 (no behaviour change).
- `build-package.ps1` refuses a version committed at any time (`git log --all`), not only one in the index: a version set back by hand rebuilt a file a rig may hold cached.
- The hourly task runs `update-workflows.ps1` from its own clone, `%LOCALAPPDATA%\lib-open-rigs\self`, set to `origin/main` each hour: unpushed commits in the working copy failed three runs in a row on 3 Nov, and a branch checked out there would have pinned every workflow to it. `push-and-update-workflows.ps1` still runs from the working copy. The task's limit is three hours (rerun `scripts\schedule.ps1` once to take it).
- A ticked branch gone from GitHub is `gone`, not `FAILED`, as rig-setup reads it, until Repos takes the tick off.
- A dependency folder with NuGet's fourth version part (`openal.redist.2.0.7.0`) is found by the dependency walk and copy, as `Get-SetupGap` already found it.
- `StreamDevices`' `ConfigRoot` defaults to `C:\Coen-Lab-Local\RigConfigs`, as `RecordHarpDevices`' (unset, it streamed nothing).
- FORMAT.md defines `completed.unmet` and rig-sync's recount of `expected[].found` (rig-setup, same round); the Manifest and HarpDevices READMEs say rig-sync copies a run that did not complete with a note and an alarm (rig-setup ddff734); the LiveView README says `ConfigRoot` can be mapped (7b02057); COEN-LAB.md says a ticked branch gone from GitHub is `gone`.
- After Phase 5 (`8162fcb`): `check-workflow-builds.ps1` compiles `src\Extensions` as Bonsai does, leaving out a folder under `src` with a project of its own; av-dome `pip/main-switchover`'s gamma calibration (MathNet, in its own `Extensions.csproj`) failed every hourly run. A nested folder with a space in its name is passed as `%20` (Phase 5 WARN).

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
- `RecordHarpDevice`: a board that reported no WhoAmI at all gets the map of the kind rig.yml names, with a note, since the manifest expects that map from rig.yml's kind (`HarpRig.RegisterMapFor`) and the experiment was held back every night otherwise; a board of another kind stops the workflow before this.
- `FORMAT.md`: `run.items` is `null` in every `running` write (so a manifest rig-sync finishes as `killed` keeps `null`); the example experiment folder has its project level.
- `scripts\hourly.ps1` holds the hourly task's body, and `schedule.ps1` registers a task that runs it, so a change to the run arrives with a pull: PIPOFFICERIG's task still ran the 2 Nov registration, with no failed-run log and success whatever happened; registered again on PIPOFFICERIG at about 19:50 on 3 Nov 2026 (first run 20:00, which stopped on this repo's unpushed commits, as it should). The morning's claim that the task's exit code reaches Task Scheduler's history was wrong: `conhost --headless` reports 0 whatever the code (measured), so `update-workflows-failed.log` is the record of a failure.
- COEN-LAB.md: only B1 is in a rig.yml; the dome, opto and acute codes are planned.

Not changed, do not raise again without new information:

- Work downstream of Go, a set or a command on the request thread (a stop sent meanwhile waits): see rig-control's BACKLOG; the README says so.
- A comment at `LiveViewPlan.cs:138` saying the stream's `RigId` is the machine name on purpose (rig.yml's `rigId` is `B1`; the live view subscribes by machine): a comment in `src\` moves every workflow's open-rigs pin; with the next LiveView change.

## Audit 2026-11-03 (/audit-rigs, set A)

Changed (tests, docs and scripts; no package raised): the Manifest tests named every experiment folder the old `_date-` way, so `SessionSection` was never tested on the name rigs write since badd9ca (both forms checked now, with `session.id`, `subject` and `started`); `FORMAT.md` says `ended-early` carries a sentence in `error`, and its example launch is a rig's (`C:\Github\Coen-Lab`, `--start`); the schedule's task ends with the script's exit code and keeps every failed run in `update-workflows-failed.log` (the hourly log was overwritten and the task read as succeeded while av-dome's setup had failed every hour since 2 Nov); the open-rigs pin ignores a README under `src\` (1e7ed99 moved every workflow's pin for one); the setup-gap message names a NuGet token the machine lacks as a cause; docs that still said `Video 0.1.0-bbox.1`, "Lab Packages On Main" (rig-setup's tick is Leave Main Alone, inverted, since 5a8b6c4) and `desk-personal` for PIPOFFICERIG.

Not changed, do not raise again without new information:

- `ControlServer` sets `GoRequested` just outside the lock that takes Go's parameter snapshot (`ControlServer.cs:551`): a `ParameterDefault` landing in that gap would show a value differing from Go's, but coen-behaviour-box fires it once, 1 s after Start; a fix forces a Control raise and a push to every workflow for a race no workflow can hit. Take it with the next Control change.
- A `Gain` set above the camera's maximum is answered ok, then stops the workflow in Ready: coen-behaviour-box gives `Gain` no `<Maximum>` and no `ParameterMaximum`; the fix is that workflow's (a maximum, or `ParameterMaximum` from the camera's `Gain.Max`), and upstream `TriggeredSpinnaker` could clamp. Raised in the audit report, not here.
- `ParameterDefault` and `ParameterMaximum` throw before ControlServer has subscribed: fails loudly; coen-behaviour-box's `DelaySubscription PT1S` covers it.
- A `ExperimentStoppedException` reaching the run input through the camera's PropertyMapping (since a92cde7) could write `error` instead of `stopped`: the include's terminal subscribes first, so teardown disposes the manifest before the camera chain sees the error; not seen.
- `Matches` counts a device's `<Name>_device.json` and `<Name>.yml` towards its folder under `auto` alone: coen-behaviour-box runs `auto;harp;RigSchema/`, whose Harp kinds are all in the WhoAmI table, so no live workflow is held back or passed wrongly. The `FORMAT.md` rule (a register map never stands in for data) is the intent; a code change waits for a workflow that hits it.
- `OpenRigs.Core` declares YamlDotNet 16.0.0 where every other package declares 16.3.0: NuGet resolves coen-behaviour-box to 16.3.0; bump Core's reference at its next pack; superseded by set C's item above (upstream's code).

## Audit 2 Nov 2026 (rig.yml-driven Harp identity, dump and register map): not changed

Fixed in the same round (HarpDevices 0.1.0-local.2, Manifest 0.1.0-local.9): the Harp-device rule was written twice, and the two disagreed on a `portName: null` and on the register map, which the manifest expected for every Harp device although `RecordHarpDevices` writes it only when a loaded package has the board's WhoAmI, so every experiment of a rig with a Lickety Split or Pico board would have been held back by rig-sync; both now use `src\Shared\HarpRig.cs`, and the map is expected only when a loaded package decodes the kind. The record was written as soon as FirmwareVersionLow arrived, but a board's dump interleaves core and app registers, so the device name, Harp serial and unique id came from the dump at connection, or were null; it now waits until the dump has been quiet for 0.2 s, still bounded by `DumpTimeout`. A device with no `.Commands` said "No register dump came within 5 s" when none was asked for; it now writes at once and says so. A board of the wrong kind stopped the workflow only after Go; the dump at connection now stops it in ready mode. The port name came from WMI's display name, where rig-setup reads the registry's `Device Parameters\PortName`; the registry comes first now. A throw in `update-workflows.ps1` escaped the hourly task's redirect, so the log stopped after "Packages:" with no reason; it is written to the log, with exit 1. Dropped: stopping at Start when a rig.yml entry with a `portName` has no matching subject, which would also stop on a `spare:` board that is ignored on purpose.

- The WhoAmI table copied from open-rigs' `devices\harp.py`: there the values are Python ClassVars, in no schema C# can read; the numbers were checked equal.
- No BoardClockSet gate on the dump: ControlServer refuses Go until Ready (`ControlServer.cs:484`), and Ready needs a triggered frame, so the clock is set first.
- The `keep` subscription swallowing errors: the same error reaches the per-experiment path.
- `Thread.Sleep` in the HarpDevices tests: about 3x margins. The Manifest tests' stand-in HarpMessage, now the real one from Harp.Behavior: no clash.
- WMI listing unplugged ports: checked, it lists present ports only.

## Audit 1 Nov 2026 (round 7 of 7: deployment and recovery)

Changed (scripts only, no package): `update-workflows.ps1` took a branch as set up once `.bonsai\Packages` existed, so a setup killed halfway, or one that found no network after `main`'s packages were copied in, was never run again and the branch's check failed every hour; it now asks for Bonsai.exe and a folder for every package `Bonsai.config` names (NuGet's `2.0.7.0` for a config's `2.0.7` included). `push-and-update-workflows.ps1` pushed a raised `<Version>` that was never built and reported every workflow up to date (round 6, 0a90299); it refuses until each package's version is in `local_packages\`.

Checked and clean: a workflow's `.bonsai\Setup.ps1` waits for Bonsai's restore when its output is captured, as rig-setup's run captures it (a copy of coen-behaviour-box with two package folders removed: both back when it exited, 4 s).

## Audit 1 Nov 2026 (round 6 of 7: live acceptance, with a person at the rig)

Changed (Control 0.1.0-local.9, Manifest 0.1.0-local.5): the manifest is the experiment's record, and it did not say who ran the experiment. rig-control kept a record of its own in gui-pyrat's `session_log.json` on the share, which the first real run could not write ("No lab server configured": the lab adapter never set up gui-pyrat's store), and which nothing read. On review it was dropped for the manifest, which travels with the data and is written however the experiment was started: `go` and `stop` may name a person (`by`, and on `go` `launched_by`, each `{"id", "name"}` or a name alone), ControlState keeps them as `LaunchedBy`, `GoBy` and `StoppedBy`, `status` reports them, and the manifest's `control` section writes them (session-manifest SPEC, version 2, same day). A controller that names nobody leaves them null; an older ControlServer ignores the fields.

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
- The hourly update-workflows meeting the Lab PIN hook: PIPOFFICERIG is a `desk-developer` machine (personal access), which rig-setup leaves without the hooks. A run on a rig (push-and-update-workflows.ps1 there) asks for the PIN, as any commit there does.
- Two ControlServers in one workflow: the second fails to bind and stops the workflow, naming the port.
- The rig.yml device walk and the build-context lookup copied in LiveView and Manifest, two readers of the workflow's commit, and console test harnesses where open-rigs uses MSTest (`src\OpenRigs.Logging.Tests`): round 5's lens, simplicity and open-rigs style.
- Rebuilding a package to compare its DLL with the committed one: builds embed the source path, so the hashes differ whatever the code.
- **Two comments still say `lab_names.yml`** (`CsvWriter.ColumnNames` and `ColumnNamesTests`): left for the next OpenRigs.Logging build, since a comment alone would rebuild and redeploy the package.
