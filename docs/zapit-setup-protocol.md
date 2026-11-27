# Zapit setup protocol

Zapit's setup phase runs inside the session workflow, between launch and Go, on a rig whose rig.yml `zapit` block names ZapitDaq's NI device (`zapit.device`). The workflow side is the Zapit-Bonsai adapter's `ConnectForTaskZapitDaq` (`OpenRigs.Opto.ZapitBonsai:ConnectForTaskZapitDaq.bonsai`; `OpenRigs.Devices:` until .26), which includes `ZapitSetup` and `ZapitCamera` (the adapter's too). The agent is the launching app. Both use only the existing channels: ControlServer commands and status (port 5561), and StreamDevices (data port 5556, video port 5558).

## Channels

- **Commands.** The agent sends `{"cmd": "command", "name": <name>, "args": <path>}`. The device `Zapit-Main` declares eight commands, and only on a rig with a `zapit` block:
  - `Zapit-Main.Setup`
  - `Zapit-Main.Still`
  - `Zapit-Main.LoadBank`
  - `Zapit-Main.LaserOff`
  - `Zapit-Main.Aim`
  - `Zapit-Main.Play`
  - `Zapit-Main.Trigger`
  - `Zapit-Main.Crop`

  A rig without the block declares none of them, so the agent finds out whether the rig has Zapit from `status.commands`.
- **State.** `status.metrics["Zapit-Main.Setup"]` holds the latest stage as `<Stage> <detail>`. The stages are:
  - `Pointing <folder or png>`
  - `FramesSaved <folder>`
  - `StillSaved <png>`
  - `Loading <json>`
  - `BankLoaded <json>`
  - `Refused <why>`

  Hand aiming has its own metric, `status.metrics["Zapit-Main.Aim"]`: `Aiming <xVolts>,<yVolts>,<laserVolts>`, `Off Go` or `Refused <why>`, so an aim never overwrites `BankLoaded`. Play Condition has its own too, `status.metrics["Zapit-Main.Play"]` (below).

  The metric keeps only the latest value. The agent sends one request, then polls until that request's terminal stage comes back: `FramesSaved`, `StillSaved`, `BankLoaded` or `Refused`.
- **Live.** The camera `Camera-Zapit` is streamed by StreamDevices as the camera Zapit, on a rig whose `zapit` block names `zapit.device`. Its frames are on `Camera-Zapit.Data`, published at `ConnectForTaskZapitDaq`'s top level (a subject inside its `Zapit` SelectMany would be out of StreamDevices' reach), and they are the frames the setup phase saves.
- **Log.** Every ZapitDaq event is a row in `WorkflowData_Events`, with `Event` set to `Zapit` and `Value` set to the trial number.

## Files

Every path the agent sends must be inside `RigConfigs\<RIG>\pending\`, the folder beside rig.yml. The names follow the ingest's Zapit file names.

| File | Writer | Content |
| --- | --- | --- |
| `pending\ZapitScanner\Frames_<DT>\grid.json` | agent | `{"volts": [[x, y], ...], "laserVolts": v, "skipFrames": 3, "backgrounds": 4, "camera": {"exposureUs": 2000, "gainDb": 0}}`. The values come from open_rigs.zapit `calibration_plan` (volts, and laser volts capped at `ceilingMw`). `camera` is optional: the Zapit camera takes that exposure and gain, auto exposure and auto gain off, from the run's first point, and its session settings back once the run ends or is refused; without it the session's settings stay. |
| `pending\ZapitScanner\Frames_<DT>\point_NNNN.png`, `background_K.png` | workflow | One lossless frame per plan point (a 4-digit, 0-based index), then the laser-off backgrounds. This is open_rigs.zapit's frame folder. |
| `pending\ZapitScanner\Accuracy_<DT>\` | both | The same layout, from `accuracy_plan`, for Measure Accuracy. |
| `pending\<any>.png` | workflow | A sample still, taken with the laser off. |
| `pending\Settings_ZapitBank_<DT>.json` and `.bin` | agent | open_rigs.zapit `build_bank`, index version 2. |

## Order

1. **Launch.** The workflow opens ZapitDaq and the Zapit camera, then reports Ready. Before step 2, the agent waits until `Zapit-Main.Setup` appears in `status.commands` and Camera-Zapit frames arrive in live view.
2. **Still.** The agent sends `Zapit-Main.Still <new png>`. The workflow zeroes the lines, saves the next frame and reports `StillSaved <png>`. The scanner settings come from the still's size, so the still comes first. The agent may take a new still as often as needed.
3. **Scanner grid.** The agent writes `grid.json` into a new `Frames_<DT>` folder and sends `Zapit-Main.Setup <folder>`.
   - For each point, the workflow points the galvos with the laser on (ZapitDaq `Point`), waits for `Pointed`, skips `skipFrames` frames and saves the next one.
   - It then saves `backgrounds` frames with the laser off at the last point.
   - Finally it zeroes every line and reports `FramesSaved <folder>`.
   - The agent fits the frames (`fit_scanner_run`). If the fit is refused, the agent says why and reruns with a new folder.
4. **Accuracy.** The same steps, in an `Accuracy_<DT>` folder (`measure_accuracy_run`).

   With `ceilingMw` 0 (laser disabled) there is no beam: the agent sends no grid, takes the nominal scanner mapping as the fit and has no accuracy.
5. **Sample.** The agent registers the still against the mouse's last reference, or asks for two clicks: bregma, then the midline point the agent names (atlas AP -3 mm, MATLAB Zapit's refAP point; they set the atlas-to-still conversion, so the 3 mm is atlas mm scaled to the mouse).
6. **Bank.** The agent builds the bank into `pending\` and checks it with open_rigs.zapit's `read_bank`. It then sends `Zapit-Main.LoadBank <json>`, and the workflow reports `Loading <json>`. ZapitDaq reads the bank and checks the `.bin` hash, the sample rate and the ±10 V range, then reports `BankLoaded <json>`. The loaded bank is also published on `Zapit.Bank`.
7. **Go.** Readiness requires all of the following:
   - the metric reads `BankLoaded` with the bank the agent built;
   - the fit, accuracy and sample have been accepted;
   - nothing was refused after them.

   At Go, if optogenetics is on, the workflow checks the loaded bank against the task: its conditions SHA-256 must match `optogenetics.zapitConditionsPath`, and every condition must hold `laserPowerMw`. If the rig has no `zapit` block, or the check fails, the workflow stops and names the reason. With no bank loaded, the first Arm stops it.
8. **After Go.** Once the workflow's experiment folder exists, the agent moves the session's files into it, before the first trial:
   - `pending\Settings_ZapitBank_<DT>.*`, the laser curve it was built from (`Settings_ZapitLaserCurve_<DT>.json`) and the conditions copy go to `Settings\`.
   - The agent writes `Settings\Settings_ZapitSample_<DT>.json` and `.png`, `Settings_ZapitScannerFit_<DT>.json` and `Settings_ZapitAccuracy_<DT>.json`.
   - `pending\ZapitScanner\Frames_<DT>\` and `Accuracy_<DT>\` go to `ZapitScanner\`, each with its `grid.json`.

   With `ceilingMw` 0 there is no `ZapitScanner\`, no accuracy file and no laser curve. ExperimentManifest expects those three under `OptogeneticsLaserEnabled`, the rest of Zapit's files under `Optogenetics`.

   ZapitDaq holds the bank in memory, so moving the files does not affect trials.

## Crop

`Zapit-Main.Crop <x,y,w,h>` cuts the Zapit camera's shown and recorded frame (the live window and the mp4) to a rectangle in camera pixels; `Zapit-Main.Crop full` (or an empty argument) puts the whole frame back. The stills and a grid run's frames stay whole, so every fit stays in the whole frame's pixels. The crop is accepted only before Go: the recording starts at Go, so its frame size never changes, and the command is ignored after it.

The answer is on its own metric, `status.metrics["Zapit-Main.Crop"]`. Only the camera knows its frame's size, so the metric answers from the camera, never from the command:

- `Cropping <x,y,w,h>` or `Cropping Full Frame`: the command was taken and the camera has not cut a frame to it yet. This state is not terminal: the stream may still be whole.
- `Cropped <x,y,w,h>`: the camera has cut a frame to this crop.
- `Full Frame`: the camera has put the whole frame back.
- `Refused <reason>; Now Full Frame` or `Refused <reason>; Now Cropped <x,y,w,h>`: terminal. The command was not a crop, or the camera's frame does not hold it. The last part says which crop the stream shows now: a crop outside the frame is dropped (`Now Full Frame`), a malformed command leaves the crop in force (`Now Cropped <x,y,w,h>`, or `Now Full Frame` when none).

Every refusal comes from the camera, which alone knows its frame size and the crop in force: the command is first answered `Cropping`, then `Refused <reason>; Now ...` from the next frame. A command is refused when it is not four whole numbers, x or y is below 0, or w or h is odd or below 16 (the crop in force stays), or when the crop runs off the frame (x+w above the width, y+h above the height; the camera drops it and shows the whole frame, never a clamped crop). The agent polls the metric until it reads `Cropped`, `Full Frame` or `Refused`, and sends Go only then, since `Cropping` can still turn into `Refused`.

`Cropping` can stay pending if the camera is not running or restarts, so the agent times out and resends the crop. The metric carries no request ID, so the agent sends one crop at a time and waits for its final answer before sending another.

## Hand aiming

Between a passed scanner fit and Go, the agent can point the beam by hand (its Point and Cat & Mouse). It sends `Zapit-Main.Aim <xVolts>,<yVolts>,<laserVolts>`: the galvo volts come from the sample mm through the scanner fit, and the laser volts from the scanner grid's power (2 mW by default, never above `ceilingMw`) through the laser curve, as `calibration_plan` makes them. The workflow points the galvos with the laser on (ZapitDaq `Point`) and reports `Aiming`. The beam stays there until the next aim, `Zapit-Main.LaserOff`, a grid run or a still. An aim or grid above the ceiling as volts (the launch property `LaserCeilingVolts`: `ceilingMw` through the laser curve) is refused, and ZapitDaq refuses any laser sample above it. Go ends a grid run or still still in progress (`Refused`, the lines zeroed), and once a trial is armed ZapitDaq answers any `Point` with `Refused`.

The workflow refuses an aim, and keeps running, when:

- `ceilingMw` is 0;
- the laser volts are outside 0 to `laserMinMaxControlVolts[1]`;
- the galvo volts are outside ±10 V, or the args are not three numbers;
- a grid run or still is in progress.

After Go it ignores aims. If the beam is still aimed at Go, it zeroes the lines (`Off Go`).

## Play Condition

Once the bank is loaded and the sample accepted, before Go, the agent can play one condition's real trial, as the task's trials play it (open_rigs.zapit `play`: `plan_play` chooses it, `ConditionPlay` runs the sequence). There is no new ZapitDaq command:

1. `Zapit-Main.Play <condition>,<powerMw>,<seconds>`: the workflow's `ZapitPlay` sends ZapitDaq its own `Arm` of that condition from the loaded bank as trial 0, laser on at that power (one of the bank's, at most `ceilingMw`), for that long with its ramp (it must fit ZapitDaq's `MaxTrialSeconds`). It reports on `status.metrics["Zapit-Main.Play"]`: `Arming <args>`, `Armed <condition>`, `Playing <condition>`, `Ended <condition> <how>`, `Off Go` or `Refused <why>`. With no bank, a play already armed, the laser disabled, or a condition, power or duration the bank cannot play, it answers `Refused` and runs on.
2. Once `Armed`, `Zapit-Main.Trigger on` raises the ZapitTrigger line (the masking light with it), which starts the trial on ZapitDaq's trigger terminal, as a trial's trigger does (the simulated twin gets its `Trigger` with it).
3. After the trial with its ramp, `Zapit-Main.Trigger off` clears the line and `Zapit-Main.LaserOff` zeroes every output.

`Zapit-Main.LaserOff` before Go also clears the line, and so does the play's own end (`Ended`: its end, a stop or a Zero), whatever the agent does; at Go a play still armed is zeroed (`Off Go`), and when the workflow stops the board piece clears the line. ZapitDaq answers a `Point`, `Staircase` or `LoadBank` while the play is armed with `Refused` (the session runs on), and after it aiming stays open: only a task's trial (1 on) closes `Point`. After Go the workflow ignores `Play` and `Trigger`.

## Refusals

The workflow reports `Refused <why>` and keeps running if a request:

- names a path outside `pending\`;
- names a folder with no `grid.json`;
- has a `grid.json` that is malformed, has volts outside ±10 V, has `skipFrames` below 0 or has `backgrounds` below 1;
- names a folder that already holds PNGs;
- names a still that is not a new `.png` in an existing folder;
- names a bank that is not a `.json`;
- arrives while another Setup or Still is in progress;
- names a bank over the rig's ceiling: built under a higher `ceilingMw`, delivering more than it, or with laser samples above `LaserCeilingVolts` (0 when the launch has no laser curve). ZapitDaq answers `Refused` and keeps any bank loaded before.

A bank that ZapitDaq refuses otherwise (bad hash, wrong sample rate, a value out of range, an unknown version) stops the workflow and names the reason, so the agent checks the bank first. After Go, the workflow ignores `Zapit-Main.Setup`, `Zapit-Main.Still`, `Zapit-Main.LoadBank` and `Zapit-Main.Aim`, and the metric stays where it was, so the agent must not send them. `Zapit-Main.LaserOff` is taken at any time: ZapitDaq `Zero` sets the galvos, laser and LED to 0 V at once, with no ramp.

## Reruns

Before Go, any step can be repeated:

- a new grid folder for the scanner fit or accuracy;
- a new still for the sample;
- a new `Zapit-Main.LoadBank`, which replaces the loaded bank.

The workflow keeps no setup state of its own beyond the loaded bank.

## Subjects the task workflows use

| Subject | Declared by | Type |
| --- | --- | --- |
| `Zapit.Commands` | ConnectForTaskZapitDaq | BehaviorSubject of ZapitDaqCommand |
| `Zapit.Events` | ConnectForTaskZapitDaq | PublishSubject of ZapitDaqEvent; ZapitBonsaiOpto.Observe takes it (the trial loop's opto feedback) |
| `Zapit.Bank` | ConnectForTaskZapitDaq | BehaviorSubject of ZapitBank; the task's draw takes it at Go (`ZapitDaqTrial.Draw`) |
| `Camera-Zapit.Data` | ZapitCamera | PublishSubject of Harp-timestamped frames |
| `Optogenetics` | ConnectForTaskZapitDaq | AsyncSubject of bool; the condition of Zapit's ExpectedFiles |
| `OptogeneticsLaserEnabled` | ConnectForTaskZapitDaq | AsyncSubject of bool, Optogenetics with rig.yml's `zapit.ceilingMw` above 0; the condition of `ZapitScanner/`, `Settings_ZapitAccuracy_` and `Settings_ZapitLaserCurve_` |
