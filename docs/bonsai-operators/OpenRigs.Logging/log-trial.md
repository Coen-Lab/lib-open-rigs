# Log Trial

`LogTrial` writes `BonsaiData_Trial`: one row per trial, holding everything about that trial, its settings and its outcome together. It is [LogData](log-data.md) with the log name fixed to `BonsaiData_Trial` and an empty `Selector`, so every field of the record is a column.

It is one of the two session files the Coen Lab agreed every rig writes besides its hardware logs (Slack #software, Nov 2026):

| File | Rows | Holds |
|---|---|---|
| `BonsaiData_Trial` | one per trial | the trial's settings, its outcome (`Correct`, `Incorrect`, `NoResponse`), the choice, the reward delivered, licks during the trial and the trial's times. Only the trial number matters, not the time. |
| `BonsaiData_Async` ([LogAsync](log-async.md)) | one per event | everything that is not a trial: manual rewards, flushes, spout moves, GUI events |

Camera trigger files and Harp register logs are hardware, not either of these.

## Usage

Build the trial's record once the trial has ended, with one field per column (an `ExpressionTransform` such as `new(Item1.Outcome as outcome, Item2.targetSide as targetSide, ...)`), stamp it last with `WithLatestTimestamp`, and send it to `LogTrial`. Start it after the session folder exists, as for any LogData (inside a `SelectMany` on `PathPrefix`, for example).

The file is `<session>\BonsaiData\BonsaiData_Trial_<date>.csv`: the folder is named by the log name up to its first underscore, so `BonsaiData_Trial` and `BonsaiData_Async` share the `BonsaiData` folder. A session running past the hour boundary that LogData chunks by leaves a second file in the same folder; read the folder's files in name order as one table.

Name the columns as the lab's [`lab_names.yml`](../../../src/open_rigs/lab_names.yml) allows: its `trialColumns` for every rig and the rig's own under `rigs`, camelCase with the unit in the name. `scripts\check-bonsai.ps1` fails a workflow whose record has any other column, and a setting that is the same all session is not a column (the manifest has the settings at Go). Never a field called `Seconds`: LogData writes the timestamp under that name.

## Live, and on disk at once

Since Logging 0.1.0-bbox.5 each row is flushed to disk as it is written (`AutoFlush`) and published to `LoggedRows` (`PublishRows`), both on in this include. Before, the rows reached disk 4 KB at a time, so rig-control's window ran tens of trials behind and a Bonsai ended by force lost its unflushed tail. OpenRigs.LiveView's `StreamDevices` (0.1.0-local.13 on) sends every published row as the `logs` stream on the data port, with its session, log name, row number (from 0, across the log's hourly files), header and line, so a live view has each row as it is written, with nothing wired in the workflow.

