# Calibration

A rig turns a quantity the task asks for, such as a reward volume, into something the hardware understands, such as a valve opening time. The relation between the two is measured once per machine and stored as a calibration artefact, which the rig reads at startup. This article covers what the platform provides for that: the artefact types, the per-machine layout and the dispense calibration.

## What the platform provides

- **Artefact types** in `OpenRigs.Core.Calibration`, generated once from the pydantic models in `open_rigs.core.calibration`. `CalibrationCurve` holds the measured `(x, y)` points with the axis names and units, provenance (`calibratedAt`, `calibratedBy`, `machineName`, `notes`), the `pulseGapMs` the samples were fired at, and the `linearFit` made from the points (`slope`, `intercept`, and the range `minX` to `maxX` it is valid for). A file without `pulseGapMs` or `linearFit` is refused. `fixtures/calibration/RewardValve-Left.json` is an example and the contract a fit writes against.
- **Fits are made in Python** when the curve is written, never in Bonsai: a workflow fires pulses and weighs, the fit is tested code outside it.
- **Pulse and weighing operators** in `OpenRigs.Core`: `FirePulses` fires a train of `(line, pulseWidthMs)` requests, timed on the Harp clock with `pulseGapMs` off-to-on between pulses, and completes once the last pulse has closed; `WeighStable` takes a weight once that step is over and the balance has settled. `WriteCalibrationArtefact` files an artefact made in Bonsai; a tool that fits in Python writes the same file itself.
- **Settings types** `DispenseProcedure` and `SerialBalance`, committed by the experiment repository as one `DispenseCalibration` YAML.
- **A serial balance include**, `OpenRigs.Devices:SerialBalance.bonsai`.
- **A manual balance include**, `OpenRigs.Devices:ManualBalance.bonsai`, for a balance with no data port. It follows the procedure's pulse requests, asks for a weight once each burst has landed, and publishes each weight typed on its entries subject as a stable `WeightMeasurement`. Run its settings through `ApplyBalancePort` with `manual` first, so each weighing takes one reading and waits long enough for a person to type it.

## Where artefacts live

Artefacts belong to the machine they were measured on. They sit under the machine's rig configuration directory, next to its `rig.yml`:

```
<ConfigRoot>\<MACHINE-NAME>\
  rig.yml
  calibration\
    RewardValve-Left.json
    RewardValve-Right.json
    _backups\
      RewardValve-Left_2026-10-05T14-30-12_calibrated.json
```

Each calibration replaces the file the rig reads and keeps a copy in `_backups\`, named `<stem>_<YYYY-MM-DDTHH-MM-SS>_<why><ext>` in UTC. `rig.yml` names each file relative to its own folder, as `calibration/RewardValve-Left.json`. See the rig configuration page for `ResolveRigConfigDirectory` and `ResolveRigFile`.

## Running a dispense calibration

The experiment repository provides a workflow that wires its own hardware: for each sample it fires its `pulses` pulses of `pulseWidthMs` with `FirePulses`, then waits with `WeighStable` for the balance to give `settleReadings` consecutive stable readings. Volume per pulse is the gain divided by the pulses and the density. Pick the pulses so the gain per sample is at least fifty times the balance's last digit. The points, the gap and the fit go into the `CalibrationCurve`.

## The rig side

In `rig.yml`, a pulsed output line is either fixed or calibrated:

```yaml
behaviorBoard:
  pulseController:
    outputs:
      DO1: { discriminatorType: CalibratedPulse, artefact: calibration/RewardValve-Left.json }
      DO2: { discriminatorType: FixedPulse, pulseWidthMs: 40 }
      DO3: { discriminatorType: FixedPulse, pulseWidthMs: 0 }
```

A rig reads the file's `linearFit` and `pulseGapMs`, never the points. Turning a requested volume into pulses from the fit is not in the platform yet.

## Other calibrations

To be built. Each fits the same schema and shape: measured points or a payload file in an artefact with provenance, stored per machine, read by the rig at startup.

- **Projector gamma**: display level against measured luminance, with the lookup table bitmap as the payload.
- **Projection mesh**: the mesh CSV as the payload.
- **Speaker filters**: the filter coefficients as the payload.
