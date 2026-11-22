# Log Controller

The `LogController` operator specifies the default parameters used to construct log paths across a session. It assembles a `PathPrefix` subject from the subject ID, session ID, root directory, and current date — this prefix is then consumed by all other logging operators (`LogData`, `LogHarpDevice`, `LogVideo`) to produce their individual file paths. There should be a `LogController` at the top of every experiment workflow. It must exist so that the `PathPrefix` subject is available for downstream loggers to subscribe to.

---

## Bonsai workflow

:::workflow
![LogController](~/assets/workflows/logging/LogController.svg){data-bonsai="~/src/OpenRigs.Logging/LogController.bonsai"}
:::

On startup the operator reads three externalized properties — `SubjectId`, `SessionId`, and `Path` — formats the current date, and publishes a single string on the `PathPrefix` subject:

```
{Path}\sub-{SubjectId}\ses-{SessionId}_{FormattedDate}
```

`FormattedDate` is the start in the machine's local time with its UTC offset, `yyyy-MM-ddTHH-mm-ss±HH-MM` (`FormatDate`, [specs/logging.md](../../../specs/logging.md) section 1). All downstream logging operators subscribe to `PathPrefix` and append their own filename segment (e.g. `_behavior.bin`, `_camera0.avi`); `GroupByTime` also reads the start from it and counts chunks from there.

With `ConfigRoot` set, the operator first checks the clock against `<ConfigRoot>\shared\lab.yml` (`timeZone`, `maxSyncAgeDays`): the machine's time zone, automatic daylight saving and Windows' last time sync. A failure stops the workflow, saying what is wrong and how to fix it, before anything is named. With no `ConfigRoot` nothing is checked. `LabClockReady` has already made the same check at Start (`LabClockAgrees`), so this one catches only a clock that went wrong between Start and Go.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `SubjectId` | `Algernon` | Identifier for the experimental subject (maps to the `sub-` BIDS prefix) |
| `SessionId` | `001` | Session identifier (maps to the `ses-` BIDS prefix) |
| `Path` | `D:\LocalData` | Root directory under which all session data are written |
| `ConfigRoot` | empty | Root holding each machine's configuration folder and `shared\lab.yml`; empty checks nothing |

---
:

