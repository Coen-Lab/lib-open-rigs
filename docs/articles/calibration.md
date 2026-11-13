# Calibration

A rig turns a quantity the task asks for, such as a reward volume, into something the hardware understands, such as a valve opening time. The relation between the two is measured once per machine and stored as a calibration artefact, which the rig reads at startup. This article covers what the platform provides for that: the artefact types, the per-machine layout, the dispense calibration, and how a rig plans a calibrated reward.

## What the platform provides

- **Artefact types** in `OpenRigs.Core.Calibration`, generated once from the pydantic models in `open_rigs.core.calibration`. `CalibrationCurve` holds the measured `(x, y)` points with the axis names and units, provenance (`calibratedAt`, `calibratedBy`, `machineName`, `notes`), the `pulseGapMs` the samples were fired at, and the `linearFit` made from the points (`slope`, `intercept`, and the range `minX` to `maxX` it is valid for). A file without `pulseGapMs` or `linearFit` is refused. `fixtures/calibration/valve-DO1.json` is an example and the contract a fit writes against.
- **Fits are made in Python** when the curve is written, never in Bonsai: a workflow fires pulses and weighs, the fit is tested code outside it.
- **Pulse and weighing operators** in `OpenRigs.Core`: `FirePulses` fires a train of `(line, widthMs)` requests for the PulseController's `TriggerCalibratedPulse`, timed on the Harp clock with `pulseGapMs` off-to-on between pulses, and completes once the last pulse has closed; `WeighStable` takes a weight once that step is over and the balance has settled; `WriteCalibrationArtefact` files the artefact.
- **Settings types** `DispenseProcedure` and `SerialBalance`, committed by the experiment repository as one `DispenseCalibration` YAML.
- **A serial balance include**, `OpenRigs.Devices:SerialBalance.bonsai`.
- **A manual balance include**, `OpenRigs.Devices:ManualBalance.bonsai` (lab addition), for a balance with no data port. It follows the procedure's pulse requests, asks for a weight once each burst has landed, and publishes each weight typed on its entries subject as a stable `WeightMeasurement`. Run its settings through `ApplyBalancePort` with `manual` first, so each weighing takes one reading and waits long enough for a person to type it.

## Where artefacts live

Artefacts belong to the machine they were measured on. They sit under the machine's rig configuration directory, next to its `rig.yml`:

```
<ConfigRoot>\<MACHINE-NAME>\
  rig.yml
  calibration\
    valve-DO1.json
    valve-DO2.json
    history\
      valve-DO1_2026-10-05T143012Z.json
```

The calibration workflow replaces the file the rig reads on every run and keeps a dated copy in `history\`. See the rig configuration page for `ResolveRigConfigDirectory` and `ResolveRigFile`.

## Running a dispense calibration

The experiment repository provides a workflow that wires its own hardware: for each sample it fires `repeats` pulses at one size with `FirePulses`, then waits with `WeighStable` for the balance to give `settleWindow` consecutive stable readings. Volume per pulse is the gain divided by the repeats and the density. Pick repeats so the gain per sample is at least fifty times the balance's last digit. The points, the gap and the fit go into the `CalibrationCurve`.

## The rig side

In `rig.yml`, a pulsed output line is either fixed or calibrated, and the board's `wiring` says what is on each line:

```yaml
behaviorBoard:
  main:
    wiring:
      DO1: { device: RewardValve, side: left }
    pulseController:
      outputs:
        DO1: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO1.json }
        DO2: { discriminatorType: FixedPulse, pulseWidthMs: 40 }
        DO3: { discriminatorType: FixedPulse, pulseWidthMs: 0 }
```
 `LoadRewardLine` reads both once at start, untyped, so it works with any rig model: the side's `RewardValve` line, its `CalibratedPulse` entry and that file's `linearFit`. It refuses, naming the line and the reason, when the file is unreadable, has no `linearFit`, was measured on another machine, or the line is not a `CalibratedPulse`; a missing file refuses, or with `MissingCalibration` `DeliverNothing` loads the line uncalibrated so its rewards deliver nothing. How old a calibration may be is not the rig's to decide.

`PlanReward` turns each requested volume into pulses and never refuses: the fewest equal pulses inside the fit's range, each `round((ul per pulse - intercept) / slope)` ms, raised to `minX` and flagged `Clamped` when the volume per pulse is below the range. The delivered volume is reported from the rounded width. `FirePulses` then fires them at the file's `pulseGapMs`, the gap the calibration was measured at.

## Other calibrations

To be built. Each fits the same schema and shape: measured points or a payload file in an artefact with provenance, stored per machine, read by the rig at startup.

- **Projector gamma**: display level against measured luminance, with the lookup table bitmap as the payload.
- **Projection mesh**: the mesh CSV as the payload.
- **Speaker filters**: the filter coefficients as the payload.
