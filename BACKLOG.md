# Backlog

Changes considered and not made, with the reason, so they are not proposed again without a new one.

## Audit 24 Nov 2026 (not recommended)

- C5, "Off Go" after LaserOff: done 26 Nov 2026 (fix-laseroff-ends-setup). LaserOff now clears the aim (`Off Laser Off`) and ends a grid run or still, so Go sends no second Zero.
- The agent's Zapit backstop skips while another Bonsai runs: not changed, as a running Bonsai may own the NI lines and zeroing them under it would fight it.
- No backstop for a Zapit-Bonsai workflow that dies before Ready (the agent marks it Zapit-Bonsai only at Ready): not changed, as before Ready no command has driven the lines, and ZapitDaq zeroes them when it opens.

## Streamed early stop, 26 Nov 2026 (open)

- One hang after a streamed trial ran dry, cause unknown: on B2's USB-6453 (Dev1), a trial cut short ran out of samples at its ramp's end, but NI's count of samples generated stayed short of the samples written, so nothing ended the trial (no Stopped in 6 s). Patched, not explained: with all its samples written, ZapitDaq's feeder asks the card whether the task is done (`IsDone`, which reports the run-out), and `ACutTrialEndsThoughTheDaqsCountStaysShortOfItsEnd` holds that. Seen once in the first idle hardware run, not in the 80 trials after the patch or in the 42-trial Bonsai session. Keep the patch; find the cause if a Stopped is ever late again.

## Opto port, 26 Nov 2026 (open)

OpenRigs.Trials now names no opto system: `TrialLoop` runs any `IOptoSystem`, and each system is an adapter package (`OpenRigs.Opto.ZapitBonsai`, `OpenRigs.Opto.ZapitMatlab`) that depends on Trials and its own driver. Left for later, each with its reason:
- **Retiring MATLAB Zapit** is now: delete `OpenRigs.Opto.ZapitMatlab` and its include, then MATLAB Zapit's pieces still in Devices (`OpenRigs.Devices.Zapit`, `ConnectForTask`, `ConnectZapit`, `ZapitLogs`, `ZapitTriggerLine` stays) and `TaskLogs`' include of `ZapitLogs`. `TrialLoop` does not change. Before deleting, move `OpenRigs.Trials.Tests`' loop tests (`TrialLoopTests`' `LoopRig` defaults to MATLAB Zapit's system) onto ZapitDaq's or a test system (`OptoTests`' `OtherOptoSystem`).
- **Neutral `opto*` trial columns** (`optoSystem`, `optoCondition`, ..., `optoDetail`, a fixed block): deferred. The `laser*` columns, `TrialTimes.Laser*`, `LaserTiming` and the event names `Arm`, `Trigger`, `LaserCap`, `LaserOff`, `TriggerOff` keep their names until then, so no task's row, schema or workflow changed with the port.
- **One `optogenetics.system` choice in the task file**: deferred. Decision-making keeps its two boxes (`optogenetics.zapitBonsai`, `optogenetics.zapitMatlab`), navigation and `open_rigs.core.task` keep `optogenetics.enabled`.
- **Per-adapter test projects**: the Zapit adapters' tests (`TrialLoopZapitDaqTests`, `ZapitDaqTrialTests`, `ZapitTrialCommandTests`, the Zapit pieces) still run in `OpenRigs.Trials.Tests`, which references both adapters (test only); `OpenRigs.Zapit` keeps `InternalsVisibleTo OpenRigs.Trials.Tests` for them.
- **Logging still forks by system** (`TaskLogs` with `ZapitLogs` vs the adapter's `TaskLogsZapitDaq`): a per-system log include passed to `TaskLogs` would end the fork.
- **Hidden dependencies older than the check** (`tests/test_package_dependencies.py` `KNOWN_HIDDEN`): Devices -> Audio, Vision, Control, Video; Logging -> Devices, Control, HarpDevices, Manifest, Video. Devices -> Vision and Logging -> Devices would be cycles if declared, so they need the include moved, not a reference. `PlayPenaltyFlash` (Vision) now declares Trials and Audio rather than taking plain values, which would change its include's inputs.
- **The lab task kit** (spouts, penalties, sounds, `ConditionedTrial`, `TrialOutcome`) is still in Trials, so Trials still depends on Devices and Audio; split it out when a second task family adopts the loop.
