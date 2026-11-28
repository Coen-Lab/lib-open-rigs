# Backlog

Changes considered and not made, with the reason, so they are not proposed again without a new one.

## Audit 24 Nov 2026 (not recommended)

- The agent's Zapit backstop skips while another Bonsai runs: not changed, as a running Bonsai may own the NI lines and zeroing them under it would fight it.
- No backstop for a Zapit-Bonsai workflow that dies before Ready (the agent marks it Zapit-Bonsai only at Ready): not changed, as before Ready no command has driven the lines, and ZapitDaq zeroes them when it opens.

## Audit 28 Nov 2026, Zapit-Bonsai and live view (not recommended)

- Catching device errors in IgnoreCompletion (or swallowing them): not done, as a failed camera or board must stop the session with its message, and in Bonsai the device's own branch errors first anyway.
- Replacing IgnoreCompletion with stock operators, or moving it into LiveViewPlan generation or the sink side: not done, as Bonsai.Reactive has no Never source and one node per template at the source is the minimum.
- Gating IgnoreCompletion to Camera-Zapit only, or wrapping Report, LoggedRows and StreamController: not done, as wrapping every device is the general rule and those sources never complete.
- Reporting ended devices in `liveview`: not done, as a frozen tile is enough for a rare case and a report adds surface.
- Changing PackVideoMessage for Mono16 frames: not done, as every B2 camera is Mono8 and Zapit converts to 8 bit itself; revisit if a lab camera goes Mono12.
- Changing the live view's resize from Area to Linear: not done, as it saves 1.4 ms at 5 frames/s and loses the anti-aliasing.
- Stretching the Zapit frame again in the rig app: not done, as the rig app's video code documents the flicker that caused.
- Gen2 garbage collection every two frames from the per-frame 8-bit and crop images: left, as it is the normal Bonsai pattern and memory stays bounded.
- A smoothed top for the percentile stretch, and a fixed floor for dark frames: left, as real skull scenes show no flicker (0.2% swing on the rig) and MinTop keeps a dim skull visible.
- Matching Mono8 truncation in the /16 grid scale: not done, as the offset is under half a count.
- Queuing crop requests so superseded ones are answered: not done, as the protocol has the agent send one at a time and resend on a timeout.
- Cropping with the camera's hardware ROI, or supporting packed Mono12p: not done, as ROI changes the stills' pixel frame (the pfs load breaks) and Mono12p is refused with a PixelFormat message.
- Rewording the crop and play grammar (`Ended 3 Ended`, the twice-echoed number in a malformed crop) or renaming the `Full Frame` stage: not done, as the agent parses it and two docs and the tests pin it.
- Dropping the Thread.Sleep(500) before the negative assert in ZapitSetupTests: left, as it can only false-pass.
- Another mW clamp, or a C# `CappedPowerMw` check, in ZapitPlay: not done, as ZapitDaq's per-sample volts check, LoadBank and Arm already bound the laser.
- Per-channel galvo or LED limits beyond NI's range: not done, as nothing needs them.
- Making ZapitPlay refusals errors: not done, as steps keep the setup session alive, which is right.
- Zeroing from inside ConditionPlay on a killed workflow: not done, as the app's backstop already zeroes the lines.
- A TakeUntil on LaserOff's Zero: not done, as it must stay open after Go.
- Refusing Trigger on in the workflow unless a play is armed, or having Go clear the line with nothing armed: not done, as the agent gates it and sends LaserOff before Go, and a high line with the AO at 0 V drives nothing.
- Making the Aim metric drop "Aiming" when a play's Arm zeroes the beam: not done, as the error is in the safe direction and Go zeroes anyway.
- Wrapping ZapitPylonCapture's observer (and ZapitSetupCapture's input OnError handlers) in Observer.Synchronize: not done, as the lock would create a new deadlock with the setup gate and the race is harmless today.
- An observer that throws from OnCompleted or OnError on the monitor thread, and a swallowed OnNext exception: not guarded, as that breaks the Rx contract and AutoDetach zeroes the lines.
- Removing the monitor's Deliver: not done, as its events would wait for the next command.
- Replacing ZapitDaq's outbox with ObserveOn(EventLoopScheduler), or waiting for events under ZapitDaq's gate again: not done, as callers and tests rely on a command's events coming inside its call and the gate wait reopens the LaserOff-during-a-grid deadlock.
- An unbounded ZapitDaq Dispose join: not done, as it risks the deadlock at stop it was meant to cure, and the lines are zeroed before the join.
- Removing Python `plan_play`'s ceiling and cycles checks because C# repeats them: not done, as the app needs the shortened plan before it sends anything and C# stays the authority.
- Removing the second validator (pydantic schema plus `rig_config`): not done, as this repo already validates twice, once for the editor schema and once at runtime.
- Hardening Python for inputs YAML and the library never produce (integers over 308 digits, non-dict combinations, `.inf`, a corrupted sample file or bank index, a null request, unchecked channel strings): not done, as the schema or C# already refuses them.
- Culture-neutral C# refusal messages: not done, as the lab culture is en-GB.
- Splitting `ZapitCameraSettings` into a separate crop type: not done, as it needs a second subject and more wiring.
- A per-kind map for ZapitDaq refusal text (`cannot LoadBank while Play Condition is armed`): not done, as the generic text over `command.Kind` is enough.
- Renaming the Play metric's `Ended <n> Ended`, or the `Zapit-Main.*` commands: not done, as they are agent-only and renaming churns the protocol and tests.
- Retitling task.py's ` in the Task's Own Parameters` and ` in Stage <label>` to lower case: not done, as the rig app's regex matches that exact text.
- Renaming "in Stage Stage Two" and replacing "trial 0" in Descriptions: not done, as they match `zapit_open_ended_laser` and `SetupTrial = 0`.
- Removing the unreachable `Trial 1 cannot arm while trial 0 still plays.`: left, as it is a harmless guard and Go zeroes an armed play first.
- Showing crop refusals to the operator, or putting operator steps in these docs: not done, as the grammar is agent-only and the app owns the steps (they go stale against the UI).
- Changing sample.py's "second point": not done, as the library cannot know the UI's name and the meaning is clear.
- Making LiveView keep aspect for every camera tile: not done, as it changes every tile and the rig app's crop-aspect logic.
- Gating StreamDevices' Camera-Zapit plan on the task file: not done, as it cannot be known at build time and IgnoreCompletion covers it.
- Line-length tidy of the new files (task.py, play.py): not done, as E501 is not enabled and the hunks follow their files.
- Re-using package versions .10, .6, .24 or `-Force` on the packaging scripts, or packing past build-package: not done, as a reused version leaves stale NuGet caches with old contents.
- A Devices release for a description-only schema edit: kept, as the generated code must match the schema, so every rig takes the new Devices.
- Moving app main's lib pin on its own: not done, as `sample_from_clicks` needs `second_ap_mm`, so the pin moves only with the rig app's Zapit branch merge.
- Checking `local_packages` against the csproj versions in this repo's Python tests: not done, as the lab's release scripts check it on every shipping path.

## Sample registration margin, 26 Nov 2026 (open)

The real B2 pair (`tests/zapit/data`, the two skull stills) registers on 33 to 36 ORB inliers on Windows and 32 on Linux CI, against `MIN_INLIERS` 30; its round trip is 19 um on Windows and 56 um on Linux (limit 25), with the same opencv lock. So a small change in the matched set moves the rigid fit by a few pixels, and `test_the_real_pair_registers_both_ways_and_the_round_trip_lands_within_25_um` is Windows-only until this is fixed. Candidates: more ORB features, or a subpixel refinement (ECC on the contrast-equalised stills) after the rigid refit. Either changes rig behaviour, so test it on B2 before merging, then drop the skip.

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
