# OpenRigs.LiveView

One node, `StreamDevices`, that streams every device a rig has to the live view, at fixed rates, from the rig's own configuration. A workflow drops it in once, as its last node, and sets `ConfigRoot`; nothing else is wired to it.

## What It Does

When the workflow is built, `StreamDevices` reads `<ConfigRoot>\<MACHINE-NAME>\rig.yml`. Every device entry there (`kind:` then `instance:`, such as `camera: box:`) has a name, `<Kind>-<Instance>` (`Camera-Box`, `BehaviorBoard-Main`). If the workflow publishes a subject called `<Kind>-<Instance>.Data` before this node, and the kind has a recipe, the device is streamed:

| Kind in rig.yml | Stream | Default | From the data |
|-----------------|--------|---------|---------------|
| `camera` | `Camera-Box.video` on the video port | 5 frames/s, 320 x 240, JPEG | the first `IplImage` in it, found by type (`Value.Image` for a frame paired with its trigger) |
| `behaviorBoard` | `BehaviorBoard-Main.encoder` on the data port | 10/s | the raw encoder count from `AnalogData` (a `HarpMessage` stream) |

Devices with no recipe, or whose data the workflow does not publish, are skipped. A `behaviorBoard` is streamed only when it is a Harp device by the rule `RecordHarpDevices` records by and `SessionManifest` expects by (`src\Shared\HarpRig.cs`: a `portName` that is not empty or `null`, and `<Name>.Data` published as `HarpMessage`), since 0.1.0-local.12; before, a board with `portName: null` was streamed though nothing recorded it. As well as the devices it sends:

- `elapsed`: seconds since the session folder appeared (`PathPrefix`, from OpenRigs.Logging), once a second.
- `liveview`: every 5 s, the workflow running (its path within its repository, the project, origin URL, branch, commit, whether tracked files differ from it) and the streams sent, the devices skipped and why, and any problem with rig.yml. The workflow is found as coen-behaviour-box's session manifest finds it, so the two name the same commit.

Every message carries the rig's name (the stream header's `rigId`, which is the machine name, not rig.yml's two-character `rigId`, since the live-view windows find a rig's streams by the machine they connect to) and the session key, `ready` until `PathPrefix` names the session folder and then that folder's last two names (`sub-<SubjectId>/ses-<SessionId>_<start>`). A workflow without `PathPrefix` keeps `ready` and sends no `elapsed`.

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
      enabled: false  # not streamed at all
```

A bad value is reported in `liveview` and the default kept. A workflow that reads rig.yml into a strict schema (coen-behaviour-box's `DeserializeFromYaml`) must declare `liveView` in that schema, or rig.yml stops it at Start.

## Ports and Other Streaming

Data on TCP `DataPort` (5556), video on `VideoPort` (5558), on every interface, opened at Start; a port in use stops the workflow there. `StreamDevices` contains OpenRigs.Streaming's StreamController (it declares `DataMessage`, `VideoMessage` and `StreamIdentity`), so a workflow uses one or the other, never both. Other streams can still be sent from the workflow with OpenRigs.Streaming's `PackDataMessage` and `PackVideoMessage`, which find those subjects.

## Things to Know

- **Last in the workflow.** Bonsai knows only the subjects built before a node, so a device published after `StreamDevices` is reported as not published.
- **Nothing throws at build.** The editor builds a workflow when it opens, on any machine: a missing or unreadable rig.yml streams no devices and says why in `liveview`.
- **The inner workflow is generated.** It is one group, `LiveView`, remade from rig.yml whenever what it would hold changes. Saving the workflow writes it out (about 190 lines for a camera and a board); reopening rebuilds it, so edits made inside it are lost.
- **Its settings live inside it.** Bonsai copies a group whenever the editor opens a workflow, the player shows a layout, or a workflow is saved, and the copy keeps only the group's name, description and inner workflow. So `ConfigRoot`, `DataPort` and `VideoPort` are kept in a `StreamDevicesSettings` node beside `LiveView` (set them on `StreamDevices`, which reads and writes that node). 0.1.0-local.2 had neither the copy's constructor nor this: it stopped the editor at open ("Constructor on type 'OpenRigs.LiveView.StreamDevices' not found"), and `--no-editor` too wherever a layout file exists. In such a copy every node is wrapped for inspection, and the generated `LiveView` is wrapped the same way.
- **`ConfigRoot` can be mapped from the workflow** (0.1.0-local.8 on): the generated inner workflow externalizes it, so a workflow property such as `--property:ConfigRoot` reaches this node as it does the rig configuration. In the lab only `check-workflow.ps1` maps it, to a temporary root; rig-control refuses a launch naming any other root (rig-control 482dd5d). Before, Bonsai refused the mapping ("The specified property 'ConfigRoot' was not found in the nested workflow"). `DataPort` and `VideoPort` are not externalized: set them on the node. Unset, `ConfigRoot` is `C:\Coen-Lab-Local\RigConfigs`, as `RecordHarpDevices`' (0.1.0-local.9 on; before, an unset one streamed nothing).
- **Bonsai internals.** Which subjects a workflow publishes is not public in Bonsai: `StreamDevices` asks Bonsai's internal build context by reflection (written against Bonsai.Core 2.9.0).

## Needs

`Bonsai.Core`, `YamlDotNet`, and in the workflow's environment `OpenRigs.Streaming`, `Bonsai.ZeroMQ`, `Bonsai.Vision` (cameras) and `Harp.Behavior` (Behavior boards).

## Tested

`dotnet run --project src\OpenRigs.LiveView.Tests` decides the plan from rig.yml files in a temporary folder, with stand-ins for the device data types: what is streamed and skipped and why, the `liveView:` settings, the session key, and the workflow's path. Exit 0 when every check passes. The inner workflow it builds is checked by building a workflow that uses it (`scripts\check-bonsai.ps1`, or the workflow's own check).
