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
| `ses-<Session>_<DateTime>` | Session identifier (`SessionId`) plus the session start time. `<Session>` is the lab's own, set by its launcher; this spec does not define it. `<DateTime>` is UTC, ISO 8601 basic form with `:` replaced by `-`, seconds resolution (for example `2026-09-10T13-27-15`). |
| `<Device>` | One directory per logged device or data stream. The name is the logger's `Name` property, up to the first underscore. |

The session directory is the unit of a dataset. Everything needed to interpret the data MUST live inside it (see sections 5 and 6).

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
- `<DateTime>` MUST be the start of the chunk the file covers (section 3), formatted as `yyyy-MM-ddTHH-mm-ss` like the session directory.

Examples:

```
sub-Algernon/ses-001_2026-09-10T13-27-15/Behavior/Behavior_2026-09-10T13-00-00.bin
sub-Algernon/ses-001_2026-09-10T13-27-15/Behavior/Behavior_32_2026-09-10T13-00-00.bin
sub-Algernon/ses-001_2026-09-10T13-27-15/Camera0/Camera0_2026-09-10T13-00-00.avi
sub-Algernon/ses-001_2026-09-10T13-27-15/Camera0/Camera0_2026-09-10T13-00-00.csv
sub-Algernon/ses-001_2026-09-10T13-27-15/LickSpout/LickSpout_2026-09-10T14-00-00.csv
```

## 3. Chunking (GroupByTime)

Every logger MUST split its output into time chunks using `GroupByTime`. The default chunk size is one hour. A new file is opened for each chunk and named after the chunk start (section 2). How chunk boundaries are aligned to the timestamp clock is an implementation detail and is not specified here, because the clock is not guaranteed to be Harp.

Rationale: a single file per device per session does not scale to video or electrophysiology. The cost of working with split files is paid once, in the Open Rigs analysis API.

Loggers MAY expose `ChunkSize` so that a rig can choose a different whole-hour chunk size. They MUST NOT offer an option to disable chunking.

## 4. Timestamps

Every logged record MUST carry a timestamp column named `Seconds`, in seconds on the rig's reference clock. For rigs using Harp this is the Harp clock (seconds since 1904-01-01T00:00:00 UTC). For tabular formats it MUST be the first column. For Harp binary files the timestamp is part of the message encoding.

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
