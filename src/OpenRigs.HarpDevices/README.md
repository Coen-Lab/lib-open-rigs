# OpenRigs.HarpDevices

One node, `RecordHarpDevices`, that checks every Harp device on a rig at Start and records it into each experiment, with nothing typed per device: everything comes from the rig's `rig.yml`. A workflow drops it in once, after its devices and `LogController`, and adds `harp` to `ExperimentManifest`'s `ExpectedFiles`.

## What It Does for Each Device

A Harp device is a device entry in rig.yml (`behaviorBoard: main:`, named `BehaviorBoard-Main`) with a `portName`, whose data the workflow publishes as `HarpMessage` under `BehaviorBoard-Main.Data`. For each one, the node adds a group to its own inner workflow, remade from rig.yml each time the workflow is built, that:

1. **At Start, checks the board.** The board on `portName` must have the USB serial rig.yml gives it as `usbSerial`: an FTDI chip's (`AB0M7PLB`, every Harp board in the lab) or a Pico's flash id (`E664C856C3913E37`), read as rig-setup's half-hourly scan reads them, so the value to put in rig.yml is the one the rig's scan (`K:\config\machines\<RIG>\status\<RIG>_Hardware.json`, `details.serialPorts`) lists. If it does not, the workflow stops, saying what is plugged in where: `COM3 holds the board AQ03WRBO, but rig.yml behaviorBoard: main: (BehaviorBoard-Main) is the board AB0M7PLB, which is on COM5: correct portName in rig.yml, or put the cables back.` An entry with no `usbSerial` stops it too, giving the line to add.
2. **When the experiment folder appears, asks for a register dump** (`RequestRegisterDump`, 0.5 s after `PathPrefix`), on `BehaviorBoard-Main.Commands`, so the experiment's logs hold every register's value at its start. The dump Bonsai.Harp asks for when it opens the port comes before the experiment folder exists. It reads OperationControl and writes it back with only `DumpRegisters` set, so the device's other settings stay as they were. With no `.Commands` subject, nothing is asked for and the build notes it. This goes when open-rigs logs the dump itself ([open-rigs#9](https://github.com/open-rigs/open-rigs/issues/9)).
3. **Writes `BehaviorBoard-Main/BehaviorBoard-Main_device.json`** once that dump has finished: the port, the USB serial, and the registers the device reported in the dump: WhoAmI, hardware, assembly, core and firmware versions, its Harp serial number and device name, and its unique id on a core from 1.12. The dump has finished when FirmwareVersionLow (register 7) has come and then no register for 0.2 s: a board sends each core register followed by one of its own (`0 32 1 33 … 7 39 … 12 44 13 …`, GEEK-BBOX1's on 2 Nov 2026), so the device name (12), Harp serial (13) and unique id (16) come after 7. Before 0.1.0-local.2 the record was written at 7 and took those three from the dump at connection, or left them null. With no dump within 5 s, or none asked for (no `.Commands`), it writes what the device reported when its port opened, with `"dumped": false` and a note saying which.
4. **Writes `BehaviorBoard-Main/BehaviorBoard-Main.yml`**, the device's register map: the `device.yml` of the Harp package loaded in the workflow whose `WhoAmI` the device reported (`Harp.Behavior` for 1216), so the map is the one that decoded the data. A package released before its generator embedded device.yml gets the lab's copy of that release's file, for that version only (`src\Shared\RegisterMaps\`, listed in `RegisterMaps.LabCopies`; since 0.1.0-local.9: `Harp.TimestampGeneratorGen3` 0.1.1, from harp-tech's a463d1d), and `register_map_from` says so. With none loaded, the record says so and no map is written, and `ExperimentManifest` does not expect one. A board that reported no WhoAmI at all gets the map of the kind rig.yml names, with a note saying so (since 0.1.0-local.3): the manifest expects that map from rig.yml's kind, so a silent board must not hold the experiment back every night, and a board of another kind stops the workflow before this.
5. **Stops the workflow if the board is of the wrong kind**: when the WhoAmI it reports is not its kind's (`behaviorBoard` 1216, `timestampGeneratorGen3` 1158, `cameraControllerGen2` 1170, `clockSynchronizer` 1152, `stepperDriver` 1130, `hobgoblin` 123, as open-rigs' `devices\harp.py`, and `licketySplit` 1400 since 0.1.0-local.8, as `devices\lickety_split.py`). Found from the dump at connection, it stops the workflow then, before Go (since 0.1.0-local.2); found from an experiment's dump, after the record is written, and the record then notes that its kind was checked only after the experiment started (since 0.1.0-local.8). A kind not in that list is recorded with its WhoAmI unchecked, and no register map is written for it, as the manifest expects none (0.1.1-coen-lab.8 on; before, a loaded package decoding the board's WhoAmI wrote one, which met the device's `<Name>/` folder entry under `auto;harp`).
6. **Publishes the record's path as `BehaviorBoard-Main.Identity`**, for any node that wants it.

`ExperimentManifest` (OpenRigs.Manifest 0.1.0-local.8 on) expects these files for each Harp device when its `ExpectedFiles` holds `harp` (`auto;harp`), so rig-sync holds back a completed experiment missing one (a run that ended any other way is copied with a note and an alarm): the record always, the register map only when a loaded Harp package has the kind's WhoAmI (0.1.0-local.9; before, a Lickety Split or any kind with no package was expected to leave a map it never writes, and every one of its experiments was held back). Which entries are Harp devices is one rule, `src\Shared\HarpRig.cs`, compiled into both packages: a `portName` that is not empty or `null`, and `<Name>.Data` published as `HarpMessage`. It is decided from rig.yml and the subjects, not from this node: Bonsai may build `ExperimentManifest` first, whatever their order in the workflow (a node fed from one built earlier is built sooner, measured 2 Nov 2026), so a subject this node publishes cannot be relied on there.

## The Wiring Check

A Behavior board's `wiring:` in rig.yml says what is plugged into each line; a workflow's `schemas\board-lines.yml` says what it uses each line for. At Start, before the board's USB serial is checked, every line the workflow uses must be in the wiring with the same `device`, the same `side` when the workflow gives one, and every `target` it needs; otherwise the workflow stops with every disagreement listed (0.1.1-coen-lab.6 on; lib-open-rigs DECISIONS.md, 7 Nov 2026), on one line separated by `; ` since 0.1.1-coen-lab.7, as rig-control keeps only the first line of a workflow's error. Two entries for one line need every target either names (coen-lab.7 on). A line the workflow does not use may be unknown. A workflow without the file, or a board the file does not list, is not checked; a file that cannot be read or parsed, whose top level is not a mapping, or whose board's entry is not a list refuses (coen-lab.8 on; before, the last two turned the check off). Each board's `<Name>_device.json` records the result as `wiring` ("checked: DO1 left RewardValve, ... agree with rig.yml's wiring", or "not checked: ..."). `part` and `note` are never compared.

```yaml
# rig.yml
behaviorBoard:
  main:
    wiring:
      DIPort0: { device: Photodiode }
      DO0: { device: CameraTrigger, targets: [Camera-Face, Camera-Pupil, Camera-Body] }
      DO1: { device: RewardValve, side: left }

# the workflow's schemas\board-lines.yml
BehaviorBoard-Main:
  - { line: cameraTriggerController.trigger0, device: CameraTrigger, targets: [Camera-Face, Camera-Pupil, Camera-Body] }
  - { line: rewardValves.left, device: RewardValve, side: left }   # a rig.yml key holding the line
  - { line: DIPort0, device: Photodiode }
```

A line is given as a channel (`DIPort0`, `DO1`), as `cameraTriggerController.trigger0` or `trigger1` (DO0 or DO1, only while rig.yml enables that trigger), or as a rig.yml key holding a DO line, looked for in the board's entry first and then at the top level (`rewardValves.left: DO1`, `zapit.triggerLine: 3`). `zapit.triggerLine` is used only while rig.yml's `zapit.hardwareTriggered` is true (coen-lab.7 on), so a rig whose Zapit TTL is not cabled may leave that line unknown. Each `device` word is one of the lab's `wiredDevices` in `K:\config\device-dictionary.json` (edited on rig-setup's Repos page, a `Wired:` line), which rig-control's names check holds a workflow's `schemas\board-lines.yml` to before launch (since 8 Nov 2026; DECISIONS.md, 7 Nov 2026).

## Settings

| Property | Default | |
|----------|---------|-|
| `ConfigRoot` | `C:\Coen-Lab-Local\RigConfigs` | The root under which each machine has its own `<MACHINE-NAME>\rig.yml`, as `StreamDevices`' setting (the same default from LiveView 0.1.0-local.9). Can be mapped from a workflow property (0.1.0-local.5 on). In the lab only `check-workflow.ps1` maps it, to a temporary root; rig-control refuses a launch naming any other root (rig-control 482dd5d). Read when the workflow is built. |

## Things to Know

- **Name the command subject `<Name>.Commands`.** The OpenRigs.Devices includes take it as `CommandsSubjectName`; a workflow that names it otherwise gets no dump at the experiment's start.
- **After the devices.** Bonsai knows only the subjects built before a node, so the node goes after the groups holding the devices and `LogController`. With no `PathPrefix` before it, it records nothing and the workflow stops at Start saying so (coen-lab.8 on; before, only its description said so, and the manifest held every run back waiting for the Harp files).
- **Its settings live inside it**, as `StreamDevices`' and `ExperimentManifest`'s do: Bonsai copies a group when the editor opens a workflow and keeps only its inner workflow.
- **An error on the board's messages stops the workflow**, before Go as after (since 0.1.0-local.8; before, one that came before the experiment folder appeared was dropped).
- **Windows only.** The USB serial is read with WMI and cfgmgr32, and each port's name from the registry's `Device Parameters\PortName`, as rig-setup reads it (since 0.1.0-local.2; the display name's `(COM3)` when there is none), and a port is plugged in when its WMI `Status` is `OK`, as rig-setup's `present` (coen-lab.8 on).

## Needs

`Bonsai.Core`, `Bonsai.Harp` and `YamlDotNet`; in the workflow, a Harp package for each kind of board (its register map), `OpenRigs.Logging`'s `LogController` and the devices' subjects as above.

## Tested

`dotnet run --project src\OpenRigs.HarpDevices.Tests` checks the USB serial parsing on the ids rig-setup's scan met (an FTDI chip, a Pico, an id Windows made up, a motherboard COM1) and this machine's own ports, the board check's every refusal, the record and register map from fake device messages (the values GEEK-BBOX1's board reported on 2 Nov 2026), a dump in that board's interleaved order recorded whole, the timeout, no dump asked for, a board of the wrong kind before and after Go, a `portName: null`, the dump request, the plan from a rig.yml, and a workflow built with the node in it, a node after it subscribing to `.Identity`, through the editor's copy, a save and a reload: exit 0 when every check passes. coen-behaviour-box's `check-workflow.ps1` builds its whole workflow with it against a filled-in rig.yml. Run on GEEK-BBOX1 since 2 Nov 2026 in coen-behaviour-box: every experiment staged since has `BehaviorBoard-Main_device.json` (`dumped: true`, AB0M7PLB on COM3, WhoAmI 1216) and the register map from Harp.Behavior 0.2.0. Not yet seen: a refusal of the wrong board on a rig.
