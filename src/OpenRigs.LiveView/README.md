# OpenRigs.LiveView

One node, `StreamDevices`, that streams every device a rig has to the live view, at fixed rates, from the rig's own configuration. A workflow drops it in once, as its last node, and sets `ConfigRoot` on the node to the folder holding the rigs' configurations; nothing is wired to it. ReadLabRig's `ConfigRoot`, the workflow's, is taken over the node's own when ReadLabRig is built first (OpenRigs.Core `WorkflowConfigRoot`), so a launch's `--property:ConfigRoot` reaches it too.

## What It Does

When the workflow is built, `StreamDevices` reads `<ConfigRoot>\<MACHINE-NAME>\rig.yml`. Every device entry there (`kind:` then `instance:`, such as `camera: overhead:`) has a name, `<Kind>-<Instance>` (`Camera-Overhead`, `BehaviorBoard-Main`). If the workflow publishes a subject called `<Kind>-<Instance>.Data` before this node, and the kind has a recipe, the device is streamed:

| Kind in rig.yml | Stream | Default | From the data |
|-----------------|--------|---------|---------------|
| `camera` | `Camera-Overhead.video` on the video port | 5 frames/s, 320 x 240, JPEG | the first `IplImage` in it, found by type (`Value.Image` for a frame paired with its trigger) |
| `behaviorBoard` | `BehaviorBoard-Main.encoder` on the data port | 10/s | the raw encoder count from `AnalogData` (a `HarpMessage` stream) |
| `behaviorBoard` | `BehaviorBoard-Main.channels` on the data port | every change; analog inputs 100/s | every channel of the board, for a controller's Board tab |
| `licketySplit` | `LicketySplit-Left.channels` on the data port | every change | `LickState`, shaped as the board's channels |
| `stepperDriver` | `StepperDriver-A.channels` on the data port | every change; positions 10/s | `MotorStopped` (sent as `{State, Changed}`, Harp.StepperDriver 0.4.0's `MotorStoppedPayload`, not a bitmask), `DigitalInputState` (home switches), `MotorRaisedError`, and `AccumulatedSteps` when the workflow has the driver send them, shaped as the board's channels |

Each `channels` message is `{"Register": <name>, "Value": {"Seconds": <board time>, "Value": <value>}}`, the register named as Harp.Behavior names it and the value with the board's own timestamp, so a 1 ms pulse can be drawn at its real length:

- every change: `DigitalInputState` (DIPort0-2, DI3), `PortDIOStateEvent` (DIO0-2 read as inputs), `OutputSet`, `OutputClear`, `OutputToggle` and `OutputState` (DOPort0-2, SupplyPort0-2, Led0-1, Rgb0-1, DO0-3, a bit mask as the board acknowledges each write), `Camera0Frame` and `Camera1Frame` (each camera trigger it sends);
- the settings that shape an output: `OutputPulseEnable` and `PulseDO0` to `PulseDO3` (milliseconds), since a DO line in pulse mode goes low by itself and the board reports no clear;
- `AnalogData` sampled at 100/s (`AnalogInput0`, `Encoder`, `AnalogInput1`, raw counts).

The board reports a state register only when it is read or written, at Start, before a window connects. So `DigitalInputState`, `PortDIOStateEvent`, `OutputState`, `OutputPulseEnable` and the pulse lengths are sent again every 2 s, unchanged and with their own timestamps (`TagRegister`'s `Repeat`); a viewer drops a message it already has.

Devices with no recipe, or whose data the workflow does not publish, are skipped. A `behaviorBoard`, `licketySplit` or `stepperDriver` is streamed only when it is a Harp device by the rule `RecordHarpDevices` records by and `ExperimentManifest` expects by (`src\Shared\HarpRig.cs`: a `portName` that is not empty or `null`, and `<Name>.Data` published as `HarpMessage`), so a board with `portName: null`, which nothing records, is not streamed. As well as the devices it sends:

- `elapsed`: seconds since the experiment folder appeared (`PathPrefix`, from OpenRigs.Logging), once a second.
- `logs`: every row a log with `PublishRows` writes, as it is written: OpenRigs.Logging's `LogTrials` and `LogEvents`, wherever in the workflow they sit. Each message is one row: `Session` (the session key), `Log` (`WorkflowData_Trials`), `Row` (its number in that log, from 0), `Header` and `Line` as written in the file. Sent only when the workflow's OpenRigs.Logging has `LoggedRows`, found among the assemblies loaded, so LiveView depends on no other lab package; with an older Logging, `liveview` says the rows are not streamed. A controller's window shows each trial from it within a second of its end.
- `liveview`: every 5 s, the workflow running (its path within its repository, the project, origin URL, branch, commit, whether tracked files differ from it) and the streams sent, the devices skipped and why, and any problem with rig.yml. The workflow is found as OpenRigs.Manifest's `ExperimentManifest` finds it (both compile `src\Shared\WorkflowFile.cs`), so the two name the same commit.

Every message carries the rig's name (the stream header's `rigId`, which is the machine name, not rig.yml's two-character `rigId`, since the live-view windows find a rig's streams by the machine they connect to) and the session key, `ready` until `PathPrefix` names the experiment folder and then that folder's last two names (`sub-<SubjectId>/ses-<SessionId>_<start>`). A workflow without `PathPrefix` keeps `ready` and sends no `elapsed`.

## Changing a Device's Rates in rig.yml

Any device entry may carry a `liveView:` block, every setting optional:

```yaml
camera:
  box:
    serialNumber: '12345678'
    liveView:
      rate: 10        # per second, up to 1000
      width: 160      # video only
      height: 120     # video only
      enabled: false  # not streamed at all; true, y, yes, on, false, n, no or off, any case, as YamlDotNet reads them
```

A bad value is reported in `liveview` and the default kept. A workflow that reads rig.yml into a strict schema (a `DeserializeFromYaml` of its own) must declare `liveView` in that schema, or rig.yml stops it at Start.

## Ports and Other Streaming

Data on TCP `DataPort` (5556), video on `VideoPort` (5558), on every interface, opened at Start; a port in use stops the workflow there. `StreamDevices` contains OpenRigs.Streaming's StreamController (it declares `DataMessage`, `VideoMessage` and `StreamIdentity`), so a workflow uses one or the other, never both. Other streams can still be sent from the workflow with OpenRigs.Streaming's `PackDataMessage` and `PackVideoMessage`, which find those subjects.

## Things to Know

- **Last in the workflow.** Bonsai knows only the subjects built before a node, so a device published after `StreamDevices` is reported as not published.
- **Nothing throws at build.** The editor builds a workflow when it opens, on any machine: a missing or unreadable rig.yml streams no devices and says why in `liveview`.
- **The inner workflow is generated.** It is one group, `LiveView`, remade from rig.yml whenever what it would hold changes. Saving the workflow writes it out (about 190 lines for a camera and a board); reopening rebuilds it, so edits made inside it are lost.
- **Its settings live inside it.** Bonsai copies a group whenever the editor opens a workflow, the player shows a layout, or a workflow is saved, and the copy keeps only the group's name, description and inner workflow. So `ConfigRoot`, `DataPort` and `VideoPort` are kept in a `StreamDevicesSettings` node beside `LiveView` (set them on `StreamDevices`, which reads and writes that node). Without the copy's constructor and this node, the editor stops at open ("Constructor on type 'OpenRigs.LiveView.StreamDevices' not found"), and `--no-editor` too wherever a layout file exists. In such a copy every node is wrapped for inspection, and the generated `LiveView` is wrapped the same way.
- **`ConfigRoot` is set on the node, never mapped**: the generated inner workflow externalizes it, but any edge into `StreamDevices` (a top-level `ExternalizedMapping` for `ConfigRoot` included) makes Bonsai build it before the device groups, and it then finds no device published and streams nothing. So a workflow holds it as a literal fed by no edge, the default when the workflow has no ReadLabRig (whose `ConfigRoot` is taken over it), and `check-workflow.ps1` sets it on the node itself, to a temporary root, as it does every other node's copy of `ConfigRoot`. `DataPort` and `VideoPort` are not externalized: set them on the node. Unset, `ConfigRoot` stops the workflow at Start naming it, as `RecordHarpDevices`' (no default, never a silent stream of nothing).
- **Bonsai internals.** Which subjects a workflow publishes is not public in Bonsai: `StreamDevices` asks Bonsai's internal build context by reflection (written against Bonsai.Core 2.9.0).

## Needs

`Bonsai.Core`, `YamlDotNet`, and in the workflow's environment `OpenRigs.Streaming`, `Bonsai.ZeroMQ`, `Bonsai.Vision` (cameras) and `Harp.Behavior` (Behavior boards).

## Tested

`dotnet run --project src\OpenRigs.LiveView.Tests` decides the plan from rig.yml files in a temporary folder, with stand-ins for the device data types: what is streamed and skipped and why, the `liveView:` settings, the session key, and the workflow's path. Exit 0 when every check passes. The inner workflow it builds is checked by building a workflow that uses it (the workflow's own check).
