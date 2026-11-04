# Calibration

A rig turns a quantity the task asks for, such as a reward volume, into something the hardware understands, such as a valve opening time. The relation between the two is measured once per machine and stored as a calibration artefact, which the rig reads at startup. This article covers what the platform provides for that: the artefact types, the per-machine layout, the dispense calibration procedure, and the rig-side switch from a fixed setting to a calibrated one.

## What the platform provides

- **Artefact types** in `OpenRigs.Core.Calibration`, generated once from the pydantic models in `open_rigs.core.calibration`. `CalibrationCurve` is the common case: measured `(x, y)` points with the axis names and units, plus provenance (`calibratedAt`, `calibratedBy`, `machineName`, `notes`). A rig fits the points at startup. The artefact never stores a fit.
- **Curve operators** `FitCalibrationCurve`, `LookupCalibration` and `SelectCalibrationPoints`, which fit the points with a chosen `CalibrationModel` and look a value up in either direction, clamping or failing outside the measured range.
- **The dispense calibration procedure**, `OpenRigs.Core:DispenseCalibration.bonsai`, for anything that delivers a dose per pulse: valves and pumps. It primes the line, weighs a baseline, fires each sample a known number of times, weighs the gain, checks the curve and writes the artefact where the rig reads it. The include is a workflow of small operators you can open and adapt: `PulseBurst` fires one sample's pulses, `WeighStable` takes a weight once the burst is over and the balance has settled, stock nodes turn the weights into gains and volumes per pulse, `BuildCalibrationCurve` checks and stamps the artefact, `WriteCalibrationArtefact` files it.
- **Settings types** `DispenseProcedure` and `SerialBalance`, committed by the experiment repository as one `DispenseCalibration` YAML.
- **A serial balance include**, `OpenRigs.Devices:SerialBalance.bonsai`.

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

The procedure replaces the file the rig reads on every run and keeps a dated copy in `history\`. See the rig configuration page for `ResolveRigConfigDirectory` and `ResolveRigFile`.

## Running a dispense calibration

The experiment repository provides a thin workflow that wires its own hardware to the platform include, for example `src/CalibrateValve.bonsai`. Its root properties are the channel to calibrate, the artefact file name, who is running it, and the COM ports of the pulse source and the balance. Nothing is read from `rig.yml`, which lets the same workflow serve a rig with several boards.

1. Deploy the experiment repository. That creates the machine's rig configuration directory.
2. Put a container on the balance under the spout and plug the balance in.
3. Open the calibration workflow, set its root properties, and start it.
4. Watch it in the editor with visualizers on the request and reading subjects and on the include's steps: priming, the baseline weight, each sample's pulses and its volume per pulse, the fit report, and finally the line to paste into `rig.yml`.
5. Repeat for the other channel.

Each sample fires `repeats` pulses at one size, then waits for the balance to give `settleWindow` consecutive stable readings. Volume per pulse is the gain divided by the repeats and the density. Pick repeats so the gain per sample is at least fifty times the balance's last digit.

The procedure refuses to write a curve that is not monotone, in either direction, because the rig inverts it to look a volume up. The error lists the points, which makes the cause visible.

## Recipes

### Harp behavior board and a serial balance

The `PulseController` include's `TriggerCalibratedPulse` subject fires one pulse of a given width on a line, which is exactly the request subject the include needs. The `SerialBalance` include publishes `WeightMeasurement` readings. The `DispenseCalibration` include names both subjects.

### Arduino or another pulse source

Not built yet. The include only needs a subject that takes `(channel, size)` requests and fires one pulse. For an Arduino that is a few nodes around `Bonsai.Arduino`'s digital output, writing high, delaying by the size, writing low.

## The rig side

In `rig.yml`, a pulsed output line is either fixed or calibrated:

```yaml
behaviorBoard:
  pulseController:
    outputs:
      DO1: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO1.json }
      DO2: { discriminatorType: FixedPulse, pulseWidthMs: 40 }
      DO3: { discriminatorType: FixedPulse, pulseWidthMs: 0 }
```

A `CalibratedPulse` entry also takes `fit` (the `CalibrationModel` the rig fits the points with, `LinearFit` by default) and `maxAgeDays` (a warning threshold, 90 by default, 0 to disable). At startup the rig reads the artefact through `ResolveRigFile`, fits it, and warns when the artefact was measured on another machine or is older than the threshold. The session folder keeps a snapshot of every artefact in use, which ties the data to the calibration that produced it.

The consuming side with platform operators alone is short: resolve the directory and the artefact (`ResolveRigFile` from the `artefact` field of the line's `outputs` entry, selected with the generated `MatchPulseOutput`), `ReadAllText`, the Core `DeserializeFromJson` into `CalibrationCurve`, `FitCalibrationCurve`, then `LookupCalibration` with `Direction` Inverse turning a requested volume into a pulse width, clamped to the calibrated range. A request of 2 µl on a curve of 0.05 µl per ms plus 0.1 µl comes back as 38 ms.

## Other calibrations

To be built. Each fits the same schema and shape: measured points or a payload file in an artefact with provenance, stored per machine, read by the rig at startup.

- **Projector gamma**: display level against measured luminance, with the lookup table bitmap as the payload.
- **Projection mesh**: the mesh CSV as the payload.
- **Speaker filters**: the filter coefficients as the payload.
