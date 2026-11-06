# OpenRigs.Control

Bonsai operators that put a workflow under an experiment controller: the workflow starts into a ready state (hardware armed, live view streaming, nothing recorded), answers a controller over ZeroMQ, and releases the experiment on Go.

## Operators

| Operator | Role |
|----------|------|
| `ExperimentControl` | The include to drop into a workflow. Runs `ControlServer`, publishes every event as `ControlEvents` and the Go event once as `ExperimentGo`. |
| `ControlServer` | Reply socket (default `@tcp://127.0.0.1:5561`, local only: the rig agent relays and checks the Lab PIN). Answers `status`, `set`, `go`, `command`, `stop`. Declares the parameters a controller may change until Go and the commands it may send. |
| `OnGo` | Passes only the Go event through; inside the include, before `ExperimentGo`. |
| `ParameterValue` | The value of a declared parameter as it stood at Go, from the `ExperimentGo` event. |
| `OnCommand` | The arguments of one declared command, each time the controller sends it. |
| `OnParameterSet` | Each new value of one declared parameter as a controller sets it before Go, for a setting the hardware takes live (camera exposure and gain while ready). Nothing until the first set. |
| `ParameterDefault` | Sets a declared parameter's value from the workflow (a rig setting) before Go, unless a controller has set it, so the controller shows the value the hardware starts with. |
| `ParameterMaximum` | Sets a declared parameter's maximum at run time (an exposure that must fit the trigger period), so a `set` above it is refused with the reason rather than stopping the workflow. |
| `ReportPhase` | Tells the controller the experiment's phase (`Ready`, `Recording`, `Finished`) on each element. Phases only move forward: a report of an earlier phase is ignored. |
| `ReportMetric` | Tells the controller the latest value of a named metric (the experiment folder, water delivered). |
| `ExperimentSettings` | From `ExperimentGo`, the experiment's settings as JSON text for OpenRigs.Logging's `LogDataSchema` named `Settings_Experiment`, so they are `Settings\Settings_Experiment_<DateTime>.json` beside the rig's and task's: `subject_id`, `session_id`, `rig`, `workflow`, `repo`, `branch`, `commit` and `dirty` (read at Start, as OpenRigs.Manifest reads them), `launched_by`, `go_by`, `go` (UTC) and `parameters`, every declared parameter as it stood at Go (0.1.1-coen.4, 7 Nov 2026; the manifest no longer copies them). |

## Protocol

One JSON object per request on a ZeroMQ REQ/REP pair, one per reply: `{"ok": true, ...}` or `{"ok": false, "why": "a sentence to show"}`.

| Request | Reply |
|---------|-------|
| `{"cmd": "status"}` | `phase`, `since`, `subject_id`, `session_id`, `go_requested`, `launched_by`, `go_by`, `stopped_by`, `parameters` (name, value, description), `commands` (name, description), `metrics` |
| `{"cmd": "set", "name": ..., "value": ...}` | Refused after Go, for an undeclared name, or for a value outside the parameter's limits. Since 0.1.0-local.10 each accepted set is emitted as a `Set` event after the reply (`OnParameterSet`). |
| `{"cmd": "go", "by": ..., "launched_by": ...}` | Refused unless the phase is `Ready`; once; and since 0.1.0-local.11 while any parameter's value is outside its limits as they stand (a value set before `ParameterMaximum` lowered the limit), naming it, so a set is wanted again. Answered before the Go event is emitted, so work downstream of Go never delays the reply; the same for `command`. Work downstream of Go, a set or a command runs on the server's request thread, so a request sent while it runs waits for it. |
| `{"cmd": "command", "name": ..., "args": ...}` | Refused unless declared and the phase is `Ready` or `Recording`. |
| `{"cmd": "stop", "why": ..., "by": ...}` | Replies, then ends the workflow with `ExperimentStoppedException`, so every file closes. Since 0.1.0-local.14 `StoppedBy` is set before `StopReason`, which OpenRigs.Manifest's stop poll fires on, so the stop it writes always carries the person. |
| A request of more than one frame | Refused, "one frame per request, got N" (since 0.1.0-local.13; before, the reply threw and the experiment ended). |

`by` and `launched_by` (0.1.0-local.9) name a person: `{"id": ..., "name": ...}` as the controller knows them, or a name alone as a string. They are optional, so a controller that names nobody still works; the workflow keeps them (`ControlState.LaunchedBy`, `GoBy`, `StoppedBy`) and reports them in `status`, and OpenRigs.Manifest writes them into the experiment's manifest. The controller vouches for them: rig-control's agent fills them in from the Lab PIN or control token behind each request, and drops any a window sent itself.

## Parameter Limits

A declared parameter may carry `Minimum`, `Maximum` and `WholeNumber`, and a `set` outside them is refused with the reason ("RunLengthMins must be a whole number from 1 to 9999; '2.5' is not"). Without limits any text is taken, and a workflow that parses the value at Go stops there instead: before 0.1.0-local.4, `set RunLengthMins "x"` was accepted and Go then stopped coen-behaviour-box with a FormatException.

```xml
<Parameter>
  <Name>RunLengthMins</Name>
  <Value>30</Value>
  <Description>How long to record, in whole minutes (1 to 9999).</Description>
  <Minimum>1</Minimum>
  <Maximum>9999</Maximum>
  <WholeNumber>true</WholeNumber>
</Parameter>
```

## Reading the Experiment from Another Package

`ControlState.Current` is the running ControlServer's state, or null when none runs (public since 0.1.0-local.5). Besides the phase, IDs, parameters and metrics, it holds `Go`, the Go event with every parameter as it stood (null before Go), and `StopReason`, the `why` of a controller's `stop` (null otherwise), and `FinishedAt`, when the workflow reported Finished (0.1.0-local.6): rig-control stops a finished experiment, which moves the phase on to Stopping, so the phase alone cannot say a run finished. Since 0.1.0-local.9 it also holds `LaunchedBy`, `GoBy` and `StoppedBy`, each a `Person` (`Id`, `Name`) or null. OpenRigs.Manifest records them all, reading them by name rather than referencing this package, so its tests check them against these names. `ControlServer` sets `Current` back to null when the workflow stops, so a reader keeps its own reference while the experiment runs.

## Dependencies

`Bonsai.Core`, `NetMQ`, `Newtonsoft.Json`.

## Tested

`dotnet run --project src\OpenRigs.Control.Tests` runs `ControlServer` on a local port (5641, not the rig's 5561) and talks to it as a controller does: parameter limits (a `NaN` is refused since 0.1.0-local.7), Go only when ready and once, who launched, sent Go and stopped (0.1.0-local.9, and a request naming nobody), commands, status, stop, live parameters (0.1.0-local.10: a `Set` event per accepted set, `ParameterDefault` leaving a controller's value alone, `ParameterMaximum` moving the limit; 0.1.0-local.11: Go refused while a value is above a maximum learned after it; 0.1.0-local.12: `ReportMetric` in the invariant culture, so `water_ml` reads `0.25` on a rig set to a comma decimal; 0.1.0-local.13: a two-frame request refused with the experiment going on, and Go and a command answered within 1 s while a subscriber spends 3 s on each), and a second server on a port in use. Exit 0 when every check passes. Since 0.1.0-local.11 an error from the socket itself ends the sequence through the observer, as a failed bind does, rather than ending Bonsai unhandled on the server's thread. Since 0.1.0-local.7 a stop no longer waits 2 s on the server's own thread before the socket is released.
