# manifest.json, version 3

One `manifest.json` in every experiment folder a rig records: what was launched, from which commit, with which settings files, what the experiment had to leave, how the run went, and every file it left.

The workflow (`ExperimentManifest`, this package) is its only writer: when the experiment folder exists, when the workflow's Finished or the controller's stop comes, and when the run ends. Nobody else ever writes it. What happens to the experiment afterwards goes in files of its own beside it, each with one writer: in the Coen Lab, `sync.json` from the overnight copy (what became of a run that never wrote `run.final`, a renamed session, the files' hashes; where it states `run` or `session`, it wins) and `ingest.json` from the server.

Every write is `manifest.json.<pid>.tmp`, then a rename over the old file, so no reader sees half a file. UTF-8 without a BOM, `\n` line ends; a reader decodes with `utf-8-sig`. Times are UTC, `YYYY-MM-DDTHH:MM:SSZ`. A field with no value is `null`, never absent. Paths in `files` and `expected` use `/`, relative to the experiment folder, no leading `./`, no `..`. No object has two keys differing only in case: Windows PowerShell 5.1 refuses to read one.

The examples are [`fixtures/manifest/v3/`](../../fixtures/manifest/v3/): `src\OpenRigs.Manifest.Tests` builds each from fixed inputs and compares it with the file byte for byte, and readers in other repositories test themselves on them. `refuse/` holds records a reader must refuse.

## Version

`version` is 3. It changes when a field a reader may rely on is removed, renamed or changes meaning; adding a field does not change it. A reader refuses a version it was not built for, and says so. Version 3 (OpenRigs.Manifest 0.1.1-coen-lab.14, 12 Nov 2026) is version 2 without `status`, `completed`, `ingest`, `files[].sha256` and `files[].expected`, which belong to the overnight copy's and the server's own files now, and with `control.launch_id` and an `ingest_script` that is `null` rather than absent.

## Fields

| Field | Meaning |
| --- | --- |
| `version` | 3. |
| `created` | When this file was last written. |
| `ingest_script` | The node's `IngestScript`: the name of the server's registry entry for this kind of data, never a path or a command; `null` when blank. |
| `writer` | `package` and `version`, the package that wrote it, its build suffix left out. |
| `rig` | `machine` (`COMPUTERNAME`), `user` (the Windows account) and `os`. |
| `session` | The session as launched: `subject` and `id` from the experiment folder's name (`sub-<Subject>/ses-<Session>_<DateTime>`, which OpenRigs.Logging's `LogController` makes from the launch's `SubjectId` and `SessionId`), `started` from its `<DateTime>`, `folder` the absolute path at the write. The name is held to `fixtures\experiment-folder\v1\names.json`: a folder not so named, or named from an ID with `_ < > : " / \ \| ? * [ ]`, a control character or a trailing dot or space, has `subject`, `id` and `started` null (since coen-lab.15; the older `ses-<Session>_date-<DateTime>` is not read). |
| `source` | The workflow that ran: `workflow` (the `.bonsai` file from the command line, relative to `root`), `repo` (`origin`), `root` (the git repository found walking up from the workflow's folder), `branch`, `commit`, `dirty` (whether `git status --porcelain` named a tracked file), `changed` (those files, from `root`, `[]` when clean), `diff_sha256` (sha256 of `git diff HEAD --binary` when `changed` is not empty, else `null`), `bonsai` (Bonsai.Core's version). `dirty` and `changed` are `null` when git did not answer. |
| `launch` | Bonsai's command line: `arguments` as given (Bonsai's own relaunch arguments left out) and `properties`, each `--property:Name=Value` (or `-p:`) by name; a name given twice, in any case, is one key with the later value. |
| `control` | The experiment controller's record (OpenRigs.Control's `ExperimentControl`), `null` when the workflow has none: `launch_id`, the id the controller passed for this launch as `--property:LaunchId` (`null` when none was); `subject_id` and `session_id` as the controller was told them; `go`, when Go was taken (`null` before); `launched_by`, `go_by`, `stopped_by`, each `{id, name}` as the controller named them (`id` empty for a name alone) or `null`. The parameters at Go are in the experiment's `Settings/Settings_Experiment_<DateTime>.json`, not here. |
| `config_files` | Each file the node's `ConfigFiles` names, read once at Start: `{path, sha256}`, or `{path, missing: true, why}`. A folder is every file in it, each on its own; `{SubjectId}` in an entry is the launch's `SubjectId`. |
| `trial_columns` | What the experiment's `BonsaiData_Trial` columns mean, from the node's `TrialColumns` file, read once at Start: `{path, sha256, columns}`, each column by name with `type` (`int`, `double`, `bool` or `string`) and, where they apply, `values`, `unit`, `formerly` and `description`; or `{path, missing: true, why}`. `null` when `TrialColumns` is blank. |
| `run` | How the run went at this write. `outcome`: `running`; `completed` (the run input completed, or the controller's phase reached Finished, even if the controller then stopped the workflow or the run input then failed); `stopped` (before that, by the controller's stop, its reason in `stop_reason`; without a controller, any stop); `ended-early` (with a controller: the workflow ended before Finished and without a stop, `error` saying so); `error` (the run input failed before Finished and the stop, `error` its message). `final`: `true` only on the end-of-run write. The workflow's Finished and the controller's stop are written as they come, with `final: false`, so a workflow killed before its end keeps its outcome; a reader acting on the end of the run treats `final: false` as `running`. `started` and `ended` (`null` while running), `seconds` between them, `items` (the run input's element count, `null` without one or while running), `error` and `stop_reason` (text or `null`). |
| `expected` | What the experiment had to leave, whether or not it was written: `{path, why, found}`. `path` is a file, a folder ending `/` that must hold a file no other entry names, or the start of a file's path ending `_`; `found` is how many files matched it at this write. |
| `notes` | Sentences on what the node could not find or decided, for showing. |
| `files` | Every file in the folder at this write, `manifest.json`, `sync.json`, `ingest.json` and any `.tmp` left out: `{path, size, mtime, closed}`. `closed` is `false` while something held the file open (Bonsai's own writers until disposal, a video writer closing its file), so its size may still change. |

Text fields (`why`, `notes`, `error`, `stop_reason`) are for showing word for word; a reader never parses them, nor a name or a path, to decide something.
