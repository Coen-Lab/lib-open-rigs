# manifest.json, version 2

One `manifest.json` in every experiment folder a rig records: what ran, from which commit, with which settings files, what the experiment had to leave, how the run ended, and every file it left. It builds on Chris Burgess's file manifest for the lab's server (11 Oct 2026), adding sections, so a reader of that format still works on it unchanged: `version`, `created`, `status`, `ingest_script` and `files` mean what they mean there.

This file is the format. What each tool does with it lives with the tool:

- **The workflow** (`ExperimentManifest`, this package) writes it when the experiment folder exists and again when the run ends, with everything the rig knows. `status` is `partial`.
- **A finishing step** checks every listed file is present and no longer being written, fills in sizes and `sha256`, and writes `status: complete`. In the Coen Lab that is rig-setup's overnight copy, which decides per experiment whether to copy, wait or raise an alarm: [rig-setup `docs/manifests.md`](https://github.com/Coen-Lab/rig-setup/blob/main/docs/manifests.md).
- **A server** treats the experiment as finished only when `status` is `complete` and every listed file matches, then runs the registered script. In the Coen Lab that is the ingest proposal in [Coen-Lab/jacuzzi-ingest](https://github.com/Coen-Lab/jacuzzi-ingest).

Every writer writes `manifest.json.tmp` and renames it to `manifest.json`, so no reader sees half a file. Paths use `/`, relative to the experiment folder, no leading `./`, no `..`. No object has two keys differing only in case: Windows PowerShell 5.1 refuses to read one.

## Example

A manifest after the finishing step, from an experiment on GEEK-BBOX1 (`files` cut to one entry, hashes shortened):

```json
{
  "version": 2,
  "created": "2026-10-30T19:57:13Z",
  "status": "complete",
  "ingest_script": "coen-behaviour-box",
  "writer": { "package": "OpenRigs.Manifest", "version": "0.1.1-coen.1" },
  "rig": {
    "machine": "GEEK-BBOX1",
    "user": "Coen lab",
    "os": "Microsoft Windows 10.0.26100"
  },
  "session": {
    "subject": "TC002",
    "id": "003B1",
    "started": "2026-10-30T19:57:12Z",
    "folder": "C:\\Coen-Lab-Local\\Acquired-Data\\sub-TC002\\ses-003B1_2026-10-30T19-57-12"
  },
  "source": {
    "workflow": "src\\main.bonsai",
    "repo": "https://github.com/ucl-open/coen-behaviour-box.git",
    "root": "C:\\Github\\Coen-Lab\\coen-behaviour-box",
    "branch": "main",
    "commit": "9b77733c1d0e8f6a4b2c9d7e5f3a1b8c6d4e2f0a",
    "dirty": true,
    "changed": ["src/main.bonsai"],
    "diff_sha256": "5d41...",
    "bonsai": "2.9.0"
  },
  "launch": {
    "arguments": ["C:\\Github\\Coen-Lab\\coen-behaviour-box\\src\\main.bonsai", "--start", "--property:SubjectId=TC002", "--property:SessionId=003B1"],
    "properties": { "SubjectId": "TC002", "SessionId": "003B1" }
  },
  "control": {
    "subject_id": "TC002",
    "session_id": "003B1",
    "go": "2026-10-30T19:57:11Z",
    "launched_by": { "id": "TomC", "name": "Tom C" },
    "go_by": { "id": "TomC", "name": "Tom C" },
    "stopped_by": null,
    "parameters": { "RunLengthMins": "30" }
  },
  "config_files": [
    { "path": "C:\\Coen-Lab-Local\\RigConfigs\\GEEK-BBOX1\\rig.yml", "sha256": "9b1c..." }
  ],
  "run": {
    "outcome": "completed",
    "started": "2026-10-30T19:57:13Z",
    "ended": "2026-10-30T20:27:14Z",
    "seconds": 1801.2,
    "items": 90000,
    "error": null,
    "stop_reason": null
  },
  "expected": [
    { "path": "BehaviorBoard-Main/", "why": "rig.yml behaviorBoard: main:, published as BehaviorBoard-Main", "found": 5 },
    { "path": "Camera-Box/", "why": "rig.yml camera: box:, published as Camera-Box", "found": 2 },
    { "path": "BehaviorBoard-Main/BehaviorBoard-Main_device.json", "why": "rig.yml behaviorBoard: main: is a Harp device (BehaviorBoard-Main.Data is HarpMessage): RecordHarpDevices' record and register map", "found": 1 },
    { "path": "BehaviorBoard-Main/BehaviorBoard-Main.yml", "why": "rig.yml behaviorBoard: main: is a Harp device (BehaviorBoard-Main.Data is HarpMessage): RecordHarpDevices' record and register map", "found": 1 },
    { "path": "RigSchema/", "why": "listed in ExpectedFiles", "found": 1 }
  ],
  "notes": [],
  "files": [
    {
      "path": "Camera-Box/Camera-Box_2026-10-30T19-00-00.avi",
      "size": 412338177,
      "mtime": "2026-10-30T20:00:00Z",
      "sha256": "a3f1...",
      "closed": true
    }
  ],
  "completed": {
    "by": "rig-sync",
    "commit": "4f2a...",
    "at": "2026-10-31T03:04:11Z",
    "missing": [],
    "unmet": []
  }
}
```

## Fields

The file manifest's own fields, unchanged:

- `version`: `1` or `2`. Version 2 (1 Nov 2026) added `launch`, `control`, `config_files`, `expected`, `notes` and the `ended-early` outcome, and dropped version 1's `rig.config_path`, `config_sha256` and `config_text`; every other version 1 field means the same. Fields added since within version 2 are optional to a reader, and `writer` says which rules a manifest followed.
- `created`: when the manifest was last written, UTC ISO 8601.
- `status`: `partial` from the workflow, `complete` from the finishing step. A reader acts only on `complete`.
- `ingest_script`: optional, the name of the script the server runs for this kind of data. It names an entry in the server's registry, never a path or a command, and carries no arguments.
- `files`: the listed files, the manifest itself excluded. `path`, `size` (bytes), `mtime` (UTC ISO 8601), `sha256` (lowercase hex; absent until the finishing step fills it in).

Written by the workflow:

- `writer` (since OpenRigs.Manifest 0.1.0-local.10, 2 Nov 2026): `package` and `version`, the package that wrote the manifest, so a reader can tell which of the rules below it followed. Absent from earlier manifests.
- `rig`: `machine` (`COMPUTERNAME`), `user` and `os`.
- `session`: `subject` and `id` from the experiment folder's name (`sub-<Subject>/ses-<Session>_<DateTime>`, and the older `ses-<Session>_date-<DateTime>`), `started` from its `<DateTime>` (UTC), `folder` the absolute path.
- `source`: the workflow that ran. `workflow` is the `.bonsai` file from the command line when there was one; `root` is the git repository found walking up from the workflow file's folder (the working directory when Bonsai was given no file), `repo` its `origin` URL, `branch` and `commit` read from `.git`; `bonsai` the Bonsai.Core version. `dirty` is whether `git status --porcelain` named any tracked file, `null` when git did not answer. Since 0.1.0-local.10: `changed`, the tracked files it named, as paths from `root` (a rename by its new path; untracked files left out, as `dirty` leaves them), `[]` when clean and `null` when git did not answer; and `diff_sha256`, the sha256 of `git diff HEAD --binary` when `changed` is not empty, else `null`. The hash cannot rebuild the change, but two experiments with the same hash ran the same code, and one taken from a candidate diff can be checked against it.
- `launch`: Bonsai's command line, `arguments` as given (Bonsai's own relaunch arguments left out) and `properties`, each `--property:Name=Value` (or `-p:`) by name; a name given twice, in any case, is one key with the later value.
- `control`: the experiment controller's settings at Go (OpenRigs.Control): `subject_id`, `session_id`, `go` (UTC) and `parameters`, each name to its value as text, and who ran it as the controller named them: `launched_by`, `go_by` and `stopped_by` (`null` for the controller's own stop after the run finished), each `{ "id", "name" }`, `id` empty when the controller gave a name alone, or `null` when it named nobody. `control` is `null` when the workflow has no experiment controller.
- `config_files`: each file the node's `ConfigFiles` names, read once at Start: `path` (absolute) and `sha256`, or `missing: true` and `why`. Which file ran, not what was in it: the settings themselves are the workflow's to log into the experiment, as the Open Rigs logging spec's settings JSON (section 6).
- `run`: `outcome` is `running` until the run ends, then `completed` (the run's sequence completed, or with no run sequence the experiment controller's phase reached Finished, even if the controller then stopped the workflow), `stopped` (stopped before that: with an experiment controller, only by the controller's stop, its reason in `stop_reason`), `ended-early` (with an experiment controller: the workflow ended before Finished and without a stop, from an error elsewhere or the editor's Stop; `error` carries a sentence saying so) or `error` (the run sequence failed, `error` carrying the message). Since 0.1.0-local.14 a `running` manifest may carry the controller's stop: it is written again as the stop comes, with `stop_reason` and `control.stopped_by`, so a workflow rig-control kills past its stop deadline keeps who stopped it and why. One more is written by rig-setup's `rig-sync.ps1`, never by this node: `killed`, when a manifest still says `running` past its `manifestStaleHours` (12) with no file of the experiment open, so the workflow died or was killed before its end-of-run write; `ended` is then rig-sync's time, `error` says so, and `notes` gains a line. `started` and `ended` UTC, `seconds` between them, `items` the number of elements the run sequence produced, `null` when none was connected and in every `running` write (so a manifest rig-sync finishes as `killed` keeps `null` whether or not a run was connected).
- `expected`: what the experiment had to leave, whether or not it was ever written: `path` (a file, or a folder ending `/` that must hold at least one file no other entry names, so a device's register map never stands in for its data), `why`, and `found`, how many files matched it at that write. A `path` is always relative to the experiment folder, with `/` and no leading `./` (since 0.1.0-local.12). By default one folder per device entry in rig.yml (`kind: instance:`, named `<Kind>-<Instance>`) whose subject the workflow publishes; with `harp` in `ExpectedFiles`, also each Harp device's `<Name>/<Name>_device.json` and `<Name>/<Name>.yml`. `files` lists only what existed at the run's end, so a device that wrote nothing shows only here.
- `files[].closed`: `true` when the workflow could open the file exclusively, so its size was final; `false` when the file was still held open at that write, by Bonsai's own writers (with a run input the final write comes at the run's end, before the loggers close: `BonsaiData_Async` is open until disposal) or another process (a video writer closing its file). Advisory: rig-sync waits for every listed file to settle and measures it again before the manifest is complete.
- `notes`: sentences on what the node could not find or decided (a config file missing, a device not expected and why, no experiment controller).

Written by the finishing step:

- `files[]`: `size` and `mtime` replaced and `sha256` filled in for every listed file; a file in the folder that the workflow did not list is added with `"expected": false`.
- `completed`: `by` (the tool), `commit` (since 2 Nov 2026: the tool's own commit, `null` when it could not be read), `at`, and `missing`, the listed paths not found, which is empty on a `complete` manifest. Since 3 Nov 2026, `unmet`: the `expected` paths with no file, `[]` when every one was met. It can be non-empty on a `complete` manifest: a run whose `outcome` is not `completed` is finished without what it never wrote (rig-setup `docs/manifests.md`), so a reader wanting a full experiment checks `run.outcome` and `completed.unmet`, not `status` alone.
- `expected[].found`: recounted by the finishing step from the copied folder, so it agrees with `completed.unmet` (the workflow's count is from its last write, which for a killed run is its first).

Written by the server:

- `ingest`: `state` (`queued`, `running`, `failed`, `done`), `script`, `started`, `finished`, `destination`, `error`. The server keeps its own job state as well; this is the copy that travels with the data.

A reader that takes a manifest as finished checks that `version` is 1 or 2, `status` is `complete`, and each listed path exists with its `size` and, when present, its `sha256`.
