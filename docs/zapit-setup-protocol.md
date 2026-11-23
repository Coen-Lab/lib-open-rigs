# Zapit setup protocol

Zapit's setup phase runs inside the session workflow, between launch and Go, on a rig whose rig.yml `zapit` block names ZapitDaq's NI device (`zapit.device`). The workflow side is OpenRigs.Devices' `ConnectForTaskZapitDaq`, which includes `ZapitSetup` and `ZapitCamera`. The agent is the launching app. Both use only the existing channels: ControlServer commands and status (port 5561), and StreamDevices (data port 5556, video port 5558).

## Channels

- **Commands.** The agent sends `{"cmd": "command", "name": <name>, "args": <path>}`. The piece `Zapit` declares four commands, and only on a rig with a `zapit` block:
  - `Zapit.Setup`
  - `Zapit.Still`
  - `Zapit.LoadBank`
  - `Zapit.LaserOff`

  A rig without the block declares none of them, so the agent finds out whether the rig has Zapit from `status.commands`.
- **State.** `status.metrics["Zapit.Setup"]` holds the latest stage as `<Stage> <detail>`. The stages are:
  - `Pointing <folder or png>`
  - `FramesSaved <folder>`
  - `StillSaved <png>`
  - `Loading <json>`
  - `BankLoaded <json>`
  - `Refused <why>`

  The metric keeps only the latest value. The agent sends one request, then polls until that request's terminal stage comes back: `FramesSaved`, `StillSaved`, `BankLoaded` or `Refused`.
- **Live.** The camera `Camera-Zapit` is streamed by StreamDevices as the camera Zapit. Its frames are on `Camera-Zapit.Data`, and they are the frames the setup phase saves.
- **Log.** Every ZapitDaq event is a row in `WorkflowData_Events`, with `Event` set to `Zapit` and `Value` set to the trial number.

## Files

Every path the agent sends must be inside `RigConfigs\<RIG>\pending\`, the folder beside rig.yml. The names follow the ingest's Zapit file names.

| File | Writer | Content |
| --- | --- | --- |
| `pending\ZapitScanner\Frames_<DT>\grid.json` | agent | `{"volts": [[x, y], ...], "laserVolts": v, "skipFrames": 3, "backgrounds": 4}`. The values come from lib_zapit `calibration_plan` (volts, and laser volts capped at `ceilingMw`). |
| `pending\ZapitScanner\Frames_<DT>\point_NNNN.png`, `background_K.png` | workflow | One lossless frame per plan point (a 4-digit, 0-based index), then the laser-off backgrounds. This is lib_zapit's frame folder. |
| `pending\ZapitScanner\Accuracy_<DT>\` | both | The same layout, from `accuracy_plan`, for Measure Accuracy. |
| `pending\<any>.png` | workflow | A sample still, taken with the laser off. |
| `pending\Settings_ZapitBank_<DT>.json` and `.bin` | agent | lib_zapit `build_bank`, index version 2. |

## Order

1. **Launch.** The workflow opens ZapitDaq and the Zapit camera, then reports Ready. Before step 2, the agent waits until `Zapit.Setup` appears in `status.commands` and Camera-Zapit frames arrive in live view.
2. **Still.** The agent sends `Zapit.Still <new png>`. The workflow zeroes the lines, saves the next frame and reports `StillSaved <png>`. The scanner settings come from the still's size, so the still comes first. The agent may take a new still as often as needed.
3. **Scanner grid.** The agent writes `grid.json` into a new `Frames_<DT>` folder and sends `Zapit.Setup <folder>`.
   - For each point, the workflow points the galvos with the laser on (ZapitDaq `Point`), waits for `Pointed`, skips `skipFrames` frames and saves the next one.
   - It then saves `backgrounds` frames with the laser off at the last point.
   - Finally it zeroes every line and reports `FramesSaved <folder>`.
   - The agent fits the frames (`fit_scanner_run`). If the fit is refused, the agent says why and reruns with a new folder.
4. **Accuracy.** The same steps, in an `Accuracy_<DT>` folder (`measure_accuracy_run`).

   With `ceilingMw` 0 (laser disabled) there is no beam: the agent sends no grid, takes the nominal scanner mapping as the fit and has no accuracy.
5. **Sample.** The agent registers the still against the mouse's last reference, or asks for bregma and lambda clicks.
6. **Bank.** The agent builds the bank into `pending\` and checks it with lib_zapit's `read_bank`. It then sends `Zapit.LoadBank <json>`, and the workflow reports `Loading <json>`. ZapitDaq reads the bank and checks the `.bin` hash, the sample rate and the ±10 V range, then reports `BankLoaded <json>`. The loaded bank is also published on `Zapit.Bank`.
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

## Refusals

The workflow reports `Refused <why>` and keeps running if a request:

- names a path outside `pending\`;
- names a folder with no `grid.json`;
- has a `grid.json` that is malformed, has volts outside ±10 V, has `skipFrames` below 0 or has `backgrounds` below 1;
- names a folder that already holds PNGs;
- names a still that is not a new `.png` in an existing folder;
- names a bank that is not a `.json`;
- arrives while another Setup or Still is in progress.

A bank that ZapitDaq refuses (bad hash, wrong sample rate, a value out of range, an unknown version) stops the workflow and names the reason, so the agent checks the bank first. After Go, the workflow ignores `Zapit.Setup`, `Zapit.Still` and `Zapit.LoadBank`, and the metric stays where it was, so the agent must not send them. `Zapit.LaserOff` is taken at any time: ZapitDaq `Zero` sets the galvos, laser and LED to 0 V at once, with no ramp.

## Reruns

Before Go, any step can be repeated:

- a new grid folder for the scanner fit or accuracy;
- a new still for the sample;
- a new `Zapit.LoadBank`, which replaces the loaded bank.

The workflow keeps no setup state of its own beyond the loaded bank.

## Subjects the task workflows use

| Subject | Declared by | Type |
| --- | --- | --- |
| `Zapit.Commands` | ConnectForTaskZapitDaq | BehaviorSubject of ZapitDaqCommand |
| `Zapit.Events` | ConnectForTaskZapitDaq | PublishSubject of ZapitDaqEvent; TrialLoop.Observe takes it |
| `Zapit.Bank` | ConnectForTaskZapitDaq | BehaviorSubject of ZapitBank; the task's draw takes it at Go (`ZapitDaqTrial.Draw`) |
| `Camera-Zapit.Data` | ZapitCamera | PublishSubject of Harp-timestamped frames |
| `Optogenetics` | ConnectForTaskZapitDaq | AsyncSubject of bool; the condition of Zapit's ExpectedFiles |
| `OptogeneticsLaserEnabled` | ConnectForTaskZapitDaq | AsyncSubject of bool, Optogenetics with rig.yml's `zapit.ceilingMw` above 0; the condition of `ZapitScanner/`, `Settings_ZapitAccuracy_` and `Settings_ZapitLaserCurve_` |
