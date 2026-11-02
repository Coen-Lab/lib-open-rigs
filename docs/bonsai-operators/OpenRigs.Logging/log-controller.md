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

All downstream logging operators subscribe to `PathPrefix` and append their own filename segment (e.g. `_behavior.bin`, `_camera0.avi`).

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `SubjectId` | `Algernon` | Identifier for the experimental subject (maps to the `sub-` BIDS prefix) |
| `SessionId` | `001` | Session identifier (maps to the `ses-` BIDS prefix) |
| `Path` | `D:\LocalData` | Root directory under which all session data are written |

---
:

