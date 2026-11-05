# Log Async

`LogAsync` writes `BonsaiData_Async`: one row per event that does not belong to a trial, such as a manual reward, a flush, a spout move or a button pressed in a GUI. It is the one file for all of them, instead of a file per kind of event. Its columns are:

| Column | Holds |
|---|---|
| `Seconds` | the Harp time of the event |
| `Event` | what happened, for example `RewardLeft` or `SpoutsIn` |
| `Detail` | any text that qualifies it, for example `Manual`; empty if none |
| `Value` | the number it carries, for example microlitres or steps; empty if none |

Its trial counterpart is [LogTrial](log-trial.md); the two share the session's `BonsaiData` folder.

## Usage

Place `LogAsync` once, at the top level of the workflow or in a top-level group, ahead of every node that sends to it, and never inside a `SelectMany`. It declares the `AsyncEvents` subject (a `ReplaySubject` of `Timestamped<AsyncEvent>`) and starts writing once `PathPrefix` exists. Because the subject replays, events sent before the session folder exists, such as a flush before Go, are kept and written when it appears.

To send an event:

1. `CreateAsyncEvent`, with `Event` and, if wanted, `Detail` set. The element becomes `Value` when it is a number or a boolean (true is 1); text becomes `Detail` when `Detail` is empty; anything else leaves `Value` empty. For events whose name or detail varies, give it a record instead: an `ExpressionTransform` such as `new(it.Name as Event, "Manual" as Detail, it.Volume as Value)` fills each of the three it has, and the properties fill the rest.
2. `WithLatestTimestamp` (Bonsai.Harp), so the event carries the Harp time.
3. `MulticastSubject` `AsyncEvents`.

Commas and line breaks in `Event` and `Detail` are written as `;` and spaces, so every event stays one row.

A typed subject declaration names itself with `<rx:Name>`, not `<Name>`; with the plain element Bonsai registers no subject and every sender fails with "The specified variable 'AsyncEvents' was not found".

## Live, and on disk at once

Since Logging 0.1.0-bbox.5 each row is flushed to disk as it is written (`AutoFlush`) and published to `LoggedRows` (`PublishRows`), both on in this include. Before, the rows reached disk 4 KB at a time, so rig-control's window ran tens of trials behind and a Bonsai ended by force lost its unflushed tail. OpenRigs.LiveView's `StreamDevices` (0.1.0-local.13 on) sends every published row as the `logs` stream on the data port, with its session, log name, row number (from 0, across the log's hourly files), header and line, so a live view has each row as it is written, with nothing wired in the workflow.
