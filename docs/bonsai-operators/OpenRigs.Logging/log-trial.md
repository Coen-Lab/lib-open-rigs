# Log Trial

`LogTrial` writes `BonsaiData_Trial`: one row per trial, holding everything about that trial, its settings and its outcome together. It is [LogData](log-data.md) with the log name fixed to `BonsaiData_Trial` and an empty `Selector`, so every field of the record is a column.

It is one of the two experiment files the Coen Lab agreed every rig writes besides its hardware logs (Slack #software, Nov 2026):

| File | Rows | Holds |
|---|---|---|
| `BonsaiData_Trial` | one per trial | the trial's settings, its outcome (`Correct`, `Incorrect`, `NoResponse`), the choice, the reward delivered, licks during the trial and the trial's times. Only the trial number matters, not the time. |
| `BonsaiData_Async` ([LogAsync](log-async.md)) | one per event | everything that is not a trial: manual rewards, flushes, spout moves, GUI events |

Camera trigger files and Harp register logs are hardware, not either of these.

## Usage

Build the trial's record once the trial has ended, with one field per column (an `ExpressionTransform` such as `new(Item1.Outcome as outcome, Item2.targetSide as targetSide, ...)`), stamp it last with `WithLatestTimestamp`, and send it to `LogTrial`. Start it after the experiment folder exists, as for any LogData (inside a `SelectMany` on `PathPrefix`, for example).

The file is `<experiment>\BonsaiData\BonsaiData_Trial_<date>.csv`: the folder is named by the log name up to its first underscore, so `BonsaiData_Trial` and `BonsaiData_Async` share the `BonsaiData` folder. An experiment running past the hour boundary that LogData chunks by leaves a second file in the same folder; read the folder's files in name order as one table.

Name and type the columns as the lab's list allows (`trialColumns` in `K:\config\data-dictionary.json`): camelCase, a Harp time ending in `Time` and in seconds, a duration or other quantity ending in its unit (`itiSeconds`, `rewardSizeUl`), and never a stored latency. app-rig-control's `scripts\check-workflow-names.ps1` refuses, on the rig, a workflow whose record has any other column, a column of another type, or lacks a required one (`trialNumber`, `outcome`, `Seconds`). What each column means (its codes, unit and former names) is the workflow's own: list every column in the repository's `schemas/trial-columns.yml` and set `ExperimentManifest`'s `TrialColumns` to it, so each experiment's manifest carries it. A setting that is the same all experiment is not a column (the manifest has the settings at Go). The record reaches `LogTrial` Harp-timestamped (`WithLatestTimestamp`, or a `Timestamped<T>` an operator makes; LogData splits its files by that time's hour), and its `Seconds` is the `Seconds` column, so the record never has a field of that name. `open_rigs.trials.read` reads the files back as one typed table.

## Live, and on disk at once

Since Logging 0.1.0-bbox.5 each row is flushed to disk as it is written (`AutoFlush`) and published to `LoggedRows` (`PublishRows`), both on in this include. Before, the rows reached disk 4 KB at a time, so app-rig-control's window ran tens of trials behind and a Bonsai ended by force lost its unflushed tail. OpenRigs.LiveView's `StreamDevices` (0.1.0-local.13 on) sends every published row as the `logs` stream on the data port, with its experiment, log name, row number (from 0, across the log's hourly files), header and line, so a live view has each row as it is written, with nothing wired in the workflow.

