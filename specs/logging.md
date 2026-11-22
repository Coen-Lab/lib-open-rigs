# Open Rigs data logging specification

Status: draft. Source of decisions: [open-rigs/roadmap#37](https://github.com/open-rigs/roadmap/issues/37).

This document defines how a standard Open Rigs experiment lays out its data on disk.

Requirement keywords (MUST, SHOULD, MAY) are used in the RFC 2119 sense.

## 1. Directory structure

All data for one session MUST be written under a single session directory:

```
<Root>/
└── sub-<Subject>/
    └── ses-<Session>_<DateTime>/
        ├── <Device>/
        │   ├── <Device>_<DateTime>.<extension>
        │   └── ...
        ├── <Device>/
        │   └── ...
        └── ...
```

| Segment | Meaning |
|---|---|
| `<Root>` | Rig-local data root, configured on `LogController` (`Path`). |
| `sub-<Subject>` | Subject identifier (`SubjectId`). |
| `ses-<Session>_<DateTime>` | Session identifier (`SessionId`) plus the session start time. `<Session>` is the lab's own, set by its launcher; this spec does not define it. `<DateTime>` is defined below. |
| `<Device>` | One directory per logged device or data stream. The name is the logger's `Name` property, up to the first underscore. |

The session directory is the unit of a dataset. Everything needed to interpret the data MUST live inside it (see sections 5 and 6).

`<DateTime>`, here and in every file name (section 2), is the rig's local wall-clock time plus its UTC offset at that instant, ISO 8601 with `:` replaced by `-`, seconds resolution: `yyyy-MM-ddTHH-mm-ss±HH-MM` (for example `2026-11-20T14-32-56+00-00`, `2026-07-20T15-32-56+01-00`, `2026-11-20T07-32-56-07-00`). Local means the machine's Windows time zone (`TimeZoneInfo.Local`). `OpenRigs.Core`'s `FormatDate` writes it from a UTC instant and `FormatDate.Parse` reads it back. The offset makes every name an exact instant, so names from rigs in different zones, or either side of a clock change, compare correctly.

Older names have no offset (`yyyy-MM-ddTHH-mm-ss`) and are UTC; every reader MUST still read them. One pattern matches both: `\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}(?:[+-]\d{2}-\d{2})?` (`open_rigs.chunks.TIME`, with `parse_time` giving the instant). Readers MUST sort and compare names by the parsed instant, never by the text: around the October clock change, and between old and new names, the text order is wrong.

Exception in this fork: before it names anything, `LogController` checks the clock and stops the workflow, saying what is wrong and how to fix it, when the check fails. With `ConfigRoot` set (`ExperimentLogs` passes `ReadLabRig`'s), it reads the lab's settings from `<ConfigRoot>\shared\lab.yml`, beside each machine's `<ConfigRoot>\<MACHINE-NAME>\` folder: `timeZone` (a Windows time zone id) and `maxSyncAgeDays` (a whole number of days, at least 1), as two plain top-level lines with comments and blank lines allowed; any other line (indented, quoted, a list or map, another key, a key twice) is refused, naming the line (fixtures/lab-settings/v1). The machine's zone MUST be `timeZone`, automatic daylight saving MUST be on, and Windows' last successful time sync MUST be within `maxSyncAgeDays` and not after the clock's present time. With no `ConfigRoot` nothing is checked.

## 2. File naming

Files are named by device and by the start time of the chunk they contain:

```
<Device>_<DateTime>.<extension>
```

Multi-channel devices, where a single device produces several independent streams (for example one Harp register per file), insert the channel between device and time:

```
<Device>_<Channel>_<DateTime>.<extension>
```

Rules:

- `<Channel>` MUST only be present for genuinely multi-channel devices. Single-stream devices MUST NOT emit a placeholder channel such as `0`.
- `<DateTime>` MUST be the start of the chunk the file covers (section 3), formatted as in section 1 with the offset in force at that chunk's own start.

Examples (a London rig in summer; the first chunk's time is the session directory's):

```
sub-Algernon/ses-001_2026-09-10T13-27-15+01-00/Behavior/Behavior_2026-09-10T13-27-15+01-00.bin
sub-Algernon/ses-001_2026-09-10T13-27-15+01-00/Behavior/Behavior_32_2026-09-10T13-27-15+01-00.bin
sub-Algernon/ses-001_2026-09-10T13-27-15+01-00/Camera0/Camera0_2026-09-10T13-27-15+01-00.avi
sub-Algernon/ses-001_2026-09-10T13-27-15+01-00/Camera0/Camera0_2026-09-10T13-27-15+01-00.csv
sub-Algernon/ses-001_2026-09-10T13-27-15+01-00/LickSpout/LickSpout_2026-09-10T14-27-15+01-00.csv
```

## 3. Chunking (GroupByTime)

Every logger MUST split its output into time chunks using `GroupByTime`. The default chunk size is one hour. Chunks count from the experiment start, the session directory's time: chunk k starts at start + k × `ChunkSize` hours, by the record's timestamp. A new file is opened for each chunk and named after the chunk start (section 2), so the first chunk's name equals the session directory's time. A record timed before the start (such as a Harp register dump at port open) goes into chunk 0, never a chunk before it. Each chunk's name carries the offset in force at its own start: a chunk starting after the October clock change carries `+00-00` where the directory carries `+01-00`. `GroupByTime` reads the start from `PathPrefix`, the session directory, its last input.

Rationale: a single file per device per session does not scale to video or electrophysiology. The cost of working with split files is paid once, in the Open Rigs analysis API.

Loggers MAY expose `ChunkSize` so that a rig can choose a different whole-hour chunk size. They MUST NOT offer an option to disable chunking.

Exception in this fork: the workflow's own logs, a row per trial or event, are small, so they are one file per experiment. They are written by `LogWorkflowData` (`GroupByExperiment` in place of `GroupByTime`) into `WorkflowData/WorkflowData_<What>_<DateTime>.csv`, where `<DateTime>` is the session directory's own start time, not a chunk's: `WorkflowData_Trials` (`LogTrials`), `WorkflowData_Events` (`LogEvents`) and a task's own, such as `WorkflowData_StimulusState`. Device logs keep the chunks.

## 4. Timestamps

Every logged record MUST carry a timestamp column named `harpTime`, in seconds on the Harp clock (seconds since 1904-01-01T00:00:00 UTC). For tabular formats it MUST be the first column. For Harp binary files the timestamp is part of the message encoding.

The column is named for its clock, so a file itself says which clock its times are on. Upstream ucl-open names it `Seconds`, after Bonsai.Harp's `Timestamped.Seconds`; this fork's readers take only `harpTime`. `LogVideo` and `LogRawVideo` are upstream's and still write `Seconds`, and `LogVideo` names its files `<Name>\<Name>.<extension>`, without the `_<DateTime>` of section 2, so its second chunk's file already exists (no lab workflow uses them; cameras log through `LogVideoFfmpeg`).

`harpTime` is the time the record was complete: the event's time for an event, the outcome for a trial (a trial's row is built and written once, when its outcome is known). It is for splitting a device's files into chunks (section 3) and lining files up with each other. A record spanning several moments, such as a trial, also names each moment as its own column ending in `Time` (`trialStartTime`, `stimulusOnsetTime`, `outcomeTime`), so an analysis reads a moment by its name, never by what the record's timestamp happens to be.

## 5. Device metadata (YAML)

For every multi-channel device (section 2), Harp or otherwise, the logger MUST save the device schema as a YAML file in the device directory, named `<Device>/<Device>.yml`. The schema MUST describe every channel that appears in the `<Channel>` position of that device's file names. For Harp devices this is the device's `device.yml`, which gives the register map needed to decode `<Device>_<Register>_<DateTime>.bin` files.

## 6. Session settings (JSON)

Every complete dataset MUST include the settings the workflow ran with, saved as JSON inside the session directory. Settings files are written once per session and are not chunked.

For a standard OpenRigs experiment there are usually three settings files, one per configuration passed to the workflow at start:

| Settings | Content |
|---|---|
| ExperimentSession | Who and what was run: subject, session, experimenter, notes. |
| Rig | Hardware configuration of the rig: devices, ports, calibrations. |
| TaskLogic | Parameters of the task or protocol. |

The file names are not strict. `RigSchema/RigSchema.json`, as written by `LogDataSchema`, is one example. Each settings file SHOULD be self-describing enough that a reader can tell which of the three it is.

## 7. Data collection principles

1. **Know which timestamps are real.** Save everything with a timestamp, but the dataset MUST make it possible to tell a hardware timestamp (for example a Harp message timestamp) from a software timestamp (for example one attached by `WithLatestFrom`). Do not mix the two in one column without recording which is which.
2. **Random numbers must be recoverable.** If a randomly drawn value cannot be recovered exactly from the logged data (for example a reward duration that is only approximately recoverable from hardware timings), the drawn value MUST be logged explicitly, for example as a software event. Alternatively use a deterministic random source and log its seed. The seed approach is only valid if the order of draws does not depend on the subject's behaviour.
3. **Sync pulses must be unambiguous.** When synchronising devices on different clocks with a shared pulse, the pulse pattern MUST contain enough randomness that alignment can be recovered even if data are missing or shifted by a frame.
