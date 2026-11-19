# Log Trials

`LogTrials` writes `WorkflowData_Trials`: one row per trial, holding everything about that trial, its settings and its outcome together. It is `LogWorkflowData` with the log name fixed to `WorkflowData_Trials` and an empty `Selector`, so every field of the record is a column. `LogWorkflowData` is [LogData](log-data.md) with one file for the experiment instead of one an hour (`GroupByExperiment`).

It is one of the two experiment files every rig writes besides its hardware logs:

| File | Rows | Holds |
|---|---|---|
| `WorkflowData_Trials` | one per trial | the trial's settings, its outcome (`correct`, `incorrect`, `noResponse`, `probe`), the choice, the reward delivered, licks during the trial and the trial's times. Only the trial number matters, not the time. |
| `WorkflowData_Events` ([LogEvents](log-events.md)) | one per event | everything that is not a trial: manual rewards, flushes, spout moves, GUI events |

Camera trigger files and Harp register logs are hardware, not either of these.

## Usage

Build the trial's record once the trial has ended, with one field per column (an `ExpressionTransform` such as `new(Item1.Outcome as outcome, Item2.targetSide as targetSide, ...)`), stamp it last with `WithLatestTimestamp`, and send it to `LogTrials`. Start it after the experiment folder exists (inside a `SelectMany` on `PathPrefix`, as `TaskLogs` does).

The file is `<experiment>\WorkflowData\WorkflowData_Trials_<start>.csv`, `<start>` the experiment folder's start time (UTC), one file however long the experiment runs: the folder is named by the log name up to its first underscore, so `WorkflowData_Trials` and `WorkflowData_Events` share the `WorkflowData` folder.

Name and type the columns as the lab's data dictionary allows (its `trialColumns`): camelCase, a Harp time ending in `Time` and in seconds, a duration or other quantity ending in its unit (`itiSeconds`, `rewardOfferedUl`), and never a stored latency. The lab's name check refuses, on the rig, a workflow whose record has any other column, a column of another type, or lacks a required one (`trialNumber`, `outcome`, `harpTime`). What each column means (its codes and unit) is the workflow's own: list every column in the repository's `schemas/trial-columns.yml` and set `ExperimentManifest`'s `TrialColumns` to it, so each experiment's manifest carries it. A setting that is the same all experiment is not a column (the manifest has the settings at Go). The record reaches `LogTrials` Harp-timestamped (`WithLatestTimestamp`, or a `Timestamped<T>` an operator makes), and its `Seconds` is written as the `harpTime` column, the time the row was complete, so the record never has a field of that name. `open_rigs.trial_log.read` reads the file back as one typed table.

## Live, and on disk at once

Each row is flushed to disk as it is written (`AutoFlush`) and published to `LoggedRows` (`PublishRows`), both on in this include: rows that reached disk 4 KB at a time put a controller's window tens of trials behind, and a Bonsai ended by force lost its unflushed tail. OpenRigs.LiveView's `StreamDevices` sends every published row as the `logs` stream on the data port, with its experiment, log name, row number (from 0), header and line, so a live view has each row as it is written, with nothing wired in the workflow.

