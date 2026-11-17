# Rig Configuration

Every rig workflow starts the same way: find the configuration directory for the machine it is running on, read `rig.yml` from it, and resolve any calibration artefact the configuration points at. `OpenRigs.Core` provides two operators for this so no rig has to carry its own copy. They deal only in paths and text. Deserialising the YAML into the rig's own generated types happens in the rig workflow, where the generated `DeserializeFromYaml` operator lives.

Only `ResolveRigConfigDirectory` knows the directory layout. `ResolveRigFile` takes `rig.yml`'s path as input, so a lab with a different convention replaces that one operator with anything that emits a directory and keeps the rest.

---

## Layout

One directory per machine under a configurable root:

```
<ConfigRoot>\
  <MACHINE-NAME>\
    rig.yml
    LickSpoutPositions.csv
    calibration\
      <artefact>.json
```

`<MACHINE-NAME>` is the Windows computer name (`Environment.MachineName`). The root has no default (`ReadLabRig`'s `ConfigRoot` sets it; unset, the workflow stops at start saying so) and can be a local folder or a network share. Paths inside `rig.yml` that point at calibration artefacts are relative to the machine directory. This allows the same YAML to work whichever root a deployment uses.

If the machine directory does not exist, the workflow fails at startup with an error naming the machine and the full path it expected. That is deliberate: a rig that refuses to start is better than a rig silently running with the wrong or a missing configuration.

---

## ResolveRigConfigDirectory

A source that emits the machine's configuration directory once.

| Property | Default | Description |
|----------|---------|-------------|
| `ConfigRoot` | none | The root under which each machine has its own configuration directory. |

Emits `<ConfigRoot>\<MACHINE-NAME>` as an absolute path, or fails at startup if the directory does not exist.

---

## ResolveRigFile

A transform that turns a path relative to a configuration directory into an absolute path, and fails if the file does not exist. It accepts either a bare relative path, resolved against the `Directory` property, or a `(rig.yml's path, path)` pair from a `Zip` of the `RigFile` subject and the path, resolved against `rig.yml`'s folder.

| Property | Default | Description |
|----------|---------|-------------|
| `Directory` | empty | The configuration directory that bare relative paths resolve against. Not used when the input pairs `rig.yml`'s path with the path. |
| `Setting` | empty | The rig.yml key the path is from, named when the path is refused. |

Feed it the relative path of a calibration artefact from the rig configuration (for example `calibration/valves.json`). Consumers then read the file as they need: `ReadAllText` followed by the generated `DeserializeFromJson` for a JSON artefact, or the path itself for an operator that loads a file, such as BonVision's mesh mapping and gamma correction includes. A full path is refused, naming `Setting` (the rig.yml key, such as `projector.meshPath`) when given: every file a rig.yml names is relative to its folder, so the same rig.yml works under any root.

---

## In a rig

The lab rig model's one reader, `OpenRigs.Devices:ReadLabRig.bonsai`, does it in one include:

1. `ResolveRigConfigDirectory`, then `rig.yml` in it, into an `AsyncSubject` named `RigFile`.
2. `SubscribeSubject RigFile`, then `ReadAllText` and the generated `DeserializeFromYaml`, into an `AsyncSubject` named `RigConfigs`.

For each artefact a piece reads: the relative path from `RigConfigs`, zipped with `SubscribeSubject RigFile`, into `ResolveRigFile`, then the consumer. Each consumer subscribes to the subjects itself.

---

## One subscription per device

When the deserialised configuration is published as a subject and several device includes take their settings from it, give each device its own `SubscribeSubject` feeding its own property mapping. A single subscription fanned out to several mappings is published by Bonsai, and the value then arrives after the devices have already subscribed with their placeholder settings.
