# OpenRigs.Control

Bonsai operators that put a workflow under an experiment controller: the workflow starts into a ready state (hardware armed, live view streaming, nothing recorded), answers a controller over ZeroMQ, and releases the experiment on Go.

## Operators

| Operator | Role |
|----------|------|
| `ExperimentControl` | The include to drop into a workflow. Runs `ControlServer`, publishes every event as `ControlEvents` and the Go event once as `ExperimentGo`. |
| `ControlServer` | Reply socket (default `@tcp://127.0.0.1:5561`, local only: the rig agent relays and checks the Lab PIN). Answers `status`, `set`, `go`, `command`, `stop`. Declares the task's parameters a controller may change until Go and the commands it may send, and gathers each piece's (`DeclareControls`). Two parameters, or two commands, with one name stop the workflow at Start, naming both, never the second shadowed by the first. |
| `OnGo` | Passes only the Go event through; inside the include, before `ExperimentGo`. |
| `DeclareControls` | A device piece's own parameters and commands (`Piece`, `Parameters`, `Commands`), declared to the running `ControlServer` from subscription to disposal, before or after it starts: status names the piece (`piece`; empty for `ExperimentControl`'s own), and a name declared twice, by two pieces or a piece and the task, stops the workflow naming both. A piece that is not included, or does not run on this rig, declares nothing. Emits nothing. |
| `ParameterValue` | The value of a declared parameter as it stood at Go, from the `ExperimentGo` event. |
| `OnCommand` | The arguments of one declared command, each time the controller sends it. |
| `OnParameterSet` | Each new value of one declared parameter as a controller sets it before Go, for a setting the hardware takes live (camera exposure and gain while ready). Nothing until the first set. |
| `ParameterDefault` | Sets a declared parameter's value from the workflow (a rig setting) before Go, unless a controller has set it, so the controller shows the value the hardware starts with. |
| `ParameterMaximum` | Sets a declared parameter's maximum at run time (an exposure that must fit the trigger period), so a `set` above it is refused with the reason rather than stopping the workflow. |
| `ReportPhase` | Tells the controller the experiment's phase (`Ready`, `Recording`, `Finished`) on each element. Phases only move forward: a report of an earlier phase is ignored. `Recording` or `Finished` before Go stops the workflow, as status would otherwise say it of an experiment nobody released. |
| `ReportMetric` | Tells the controller the latest value of a named metric (the experiment folder, water delivered). |
| `ExperimentSettings` | From `ExperimentGo`, the experiment's settings as JSON text for OpenRigs.Logging's `LogDataSchema` named `Settings_Experiment`, so they are `Settings\Settings_Experiment_<DateTime>.json` beside the rig's and task's: `subject_id`, `session_id`, `rig`, `workflow`, `repo`, `branch`, `commit` and `dirty` (read when the node is subscribed, at the workflow's start, not at Go, as OpenRigs.Manifest reads them), `launched_by`, `go_by`, `go` (UTC) and `parameters`, every declared parameter as it stood at Go (the manifest does not copy them). |

## Protocol

One JSON object per request on a ZeroMQ REQ/REP pair, one per reply: `{"ok": true, ...}` or `{"ok": false, "why": "a sentence to show"}`.

| Request | Reply |
|---------|-------|
| `{"cmd": "status"}` | `phase`, `since`, `subject_id`, `session_id`, `go_requested`, `launched_by`, `go_by`, `stopped_by`, `parameters` (name, value, description, `piece`), `commands` (name, description, `piece`, `taken`), `metrics`. `taken` counts the times the command was taken (an `ok` reply, its event emitted), so a controller whose `command` timed out can compare it with the count before to tell whether it ran. |
| `{"cmd": "set", "name": ..., "value": ...}` | Refused after Go, for an undeclared name, or for a value outside the parameter's limits. Each accepted set is emitted as a `Set` event after the reply (`OnParameterSet`). |
| `{"cmd": "go", "by": ..., "launched_by": ...}` | Refused unless the phase is `Ready`; once; and while any parameter's value is outside its limits as they stand (a value set before `ParameterMaximum` lowered the limit), naming it, so a set is wanted again. Answered before the Go event is emitted, so work downstream of Go never delays the reply; the same for `command`. Work downstream of Go, a set or a command runs on the server's request thread, so a request sent while it runs waits for it. |
| `{"cmd": "command", "name": ..., "args": ...}` | Refused unless declared and the phase is `Ready` or `Recording`. |
| `{"cmd": "stop", "why": ..., "by": ...}` | Replies, then ends the workflow with `ExperimentStoppedException`, so every file closes. `StoppedBy` is set before `StopReason`, which OpenRigs.Manifest's stop poll fires on, so the stop it writes always carries the person. |
| A request of more than one frame | Refused, "one frame per request, got N", with the experiment going on. |

`by` and `launched_by` name a person: `{"id": ..., "name": ...}` as the controller knows them, or a name alone as a string. They are optional, so a controller that names nobody still works; the workflow keeps them (`ControlState.LaunchedBy`, `GoBy`, `StoppedBy`) and reports them in `status`, and OpenRigs.Manifest writes them into the experiment's manifest. The controller vouches for them: its rig agent fills them in from the Lab PIN or control token behind each request, and drops any a window sent itself.

## Parameter Limits

A declared parameter may carry `Minimum`, `Maximum` and `WholeNumber`, and a `set` outside them is refused with the reason ("RunLengthMinutes must be a whole number from 1 to 9999; '2.5' is not"). Without limits any text is taken, and a workflow that parses the value at Go stops there instead (`set RunLengthMinutes "x"` taken, then a FormatException at Go).

```xml
<Parameter>
  <Name>RunLengthMinutes</Name>
  <Value>30</Value>
  <Description>How long to record, in whole minutes (1 to 9999).</Description>
  <Minimum>1</Minimum>
  <Maximum>9999</Maximum>
  <WholeNumber>true</WholeNumber>
</Parameter>
```

## Reading the Experiment from Another Package

`ControlState.Current` is the running ControlServer's state, or null when none runs. Besides the phase, IDs, parameters and metrics, it holds `Go`, the Go event with every parameter as it stood (null before Go), and `StopReason`, the `why` of a controller's `stop` (null otherwise), and `FinishedAt`, when the workflow reported Finished: the controller stops a finished experiment, which moves the phase on to Stopping, so the phase alone cannot say a run finished. It also holds `LaunchedBy`, `GoBy` and `StoppedBy`, each a `Person` (`Id`, `Name`) or null. OpenRigs.Manifest records them all, reading them by name rather than referencing this package, so its tests check them against these names. `ControlServer` sets `Current` back to null when the workflow stops, so a reader keeps its own reference while the experiment runs.

## Dependencies

`Bonsai.Core`, `NetMQ`, `Newtonsoft.Json`.

## Tested

`dotnet run --project src\OpenRigs.Control.Tests` runs `ControlServer` on a local port (5641, not the rig's 5561) and talks to it as a controller does, checking each row of the tables above, `ReportMetric` in the invariant culture (`water_ml` reads `0.25` on a rig set to a comma decimal), Go and a command answered within 1 s while a subscriber spends 3 s on each, and a second server on a port in use. Exit 0 when every check passes. An error from the socket itself ends the sequence through the observer, as a failed bind does, rather than ending Bonsai unhandled on the server's thread; a stop never waits on the server's own thread before the socket is released.
