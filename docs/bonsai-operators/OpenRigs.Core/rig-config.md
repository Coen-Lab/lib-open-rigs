# Rig Configuration

Every rig workflow starts the same way: find the configuration directory for the machine it is running on, read `rig.yml` from it, and resolve any calibration artefact the configuration points at. `OpenRigs.Core` provides three operators for this so no rig has to carry its own copy. They deal only in paths and text. Deserialising the YAML into the rig's own generated types happens in the rig workflow, where the generated `DeserializeFromYaml` operator lives.

Only one of the three, `ResolveRigConfigDirectory`, knows the directory layout. The other two take a directory as input, so a lab with a different convention replaces that one operator with anything that emits a directory and keeps the rest.

---

## Layout

One directory per machine under a configurable root:

```
<ConfigRoot>\
  <MACHINE-NAME>\
    rig.yml
    calibration\
      <artefact>.json
```

`<MACHINE-NAME>` is the Windows computer name (`Environment.MachineName`). The root defaults to `C:\RigConfigs` and can be a local folder or a network share. Paths inside `rig.yml` that point at calibration artefacts are relative to the machine directory. This allows the same YAML to work whichever root a deployment uses.

If the machine directory does not exist, the workflow fails at startup with an error naming the machine and the full path it expected. That is deliberate: a rig that refuses to start is better than a rig silently running with the wrong or a missing configuration.

---

## ResolveRigConfigDirectory

A source that emits the machine's configuration directory once.

| Property | Default | Description |
|----------|---------|-------------|
| `ConfigRoot` | `C:\RigConfigs` | The root under which each machine has its own configuration directory. |

Emits `<ConfigRoot>\<MACHINE-NAME>` as an absolute path, or fails at startup if the directory does not exist.

---

## ReadRigConfig

An include that takes the configuration directory as input, reads the rig configuration file from it, and emits the text once.

:::workflow
![ReadRigConfig](~/assets/workflows/core/ReadRigConfig.svg){data-bonsai="~/src/OpenRigs.Core/ReadRigConfig.bonsai"}
:::

| Property | Default | Description |
|----------|---------|-------------|
| `FileName` | `rig.yml` | The name of the rig configuration file inside the directory. |

The usual continuation is the generated `DeserializeFromYaml` for the rig's own `Rig` type, followed by an `AsyncSubject` named after the configuration (for example `RigConfig`) that the rest of the workflow subscribes to. Fails at startup if the file is missing.

---

## ResolveRigFile

A transform that turns a path relative to a configuration directory into an absolute path, and fails if the file does not exist. It accepts either a bare relative path, resolved against the `Directory` property, or a `(directory, path)` pair, for example from a `Zip` of the directory subject and the path.

| Property | Default | Description |
|----------|---------|-------------|
| `Directory` | empty | The configuration directory that bare relative paths resolve against. Not used when the input pairs the directory with the path. |

Feed it the relative path of a calibration artefact from the rig configuration (for example `calibration/valves.json`). Consumers then read the file as they need: `ReadAllText` followed by the generated `DeserializeFromJson` for a JSON artefact, or the path itself for an operator that loads a file, such as BonVision's mesh mapping and gamma correction includes. A rooted input path is checked as given and passed through, so an absolute override still works.

---

## In a rig

Three lines at the top of the workflow:

1. `ResolveRigConfigDirectory` into an `AsyncSubject` named `RigConfigDirectory`.
2. `SubscribeSubject RigConfigDirectory`, then `ReadRigConfig`, then the generated `DeserializeFromYaml`, into an `AsyncSubject` named `RigConfig`.
3. For each artefact: the relative path from `RigConfig`, zipped with `SubscribeSubject RigConfigDirectory`, into `ResolveRigFile`, then the consumer.

The two subjects belong to the rig workflow, not the package, so each consumer subscribes to them itself.

---

## One subscription per device

When the deserialised configuration is published as a subject and several device includes take their settings from it, give each device its own `SubscribeSubject` feeding its own property mapping. A single subscription fanned out to several mappings is published by Bonsai, and the value then arrives after the devices have already subscribed with their placeholder settings.
