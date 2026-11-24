# Lick Spout Stage

The lick spout stage positions a pair of lick spouts in front of the animal on five stepper axes, driven by two Harp StepperDriver boards: board `a` drives the base transverse, left radial and right radial axes, board `b` the left and right elevations. `LickSpoutStage` is its device piece (see [Building Blocks](../../articles/building-blocks.md)).

---

## rig.yml

```yaml
stepperDriver:
  a: { portName: COM3, usbSerial: CHANGE-ME }
  b: { portName: COM4, usbSerial: CHANGE-ME }
lickSpoutDefaultPosition: { baseTransverse: 0, radialLeft: 0, radialRight: 0, elevationLeft: 0, elevationRight: 0 }
```

The rig's spout positions are one list, `LickSpoutPositions.csv` beside `rig.yml`: a header, `mouse,baseTransverse,radialLeft,radialRight,elevationLeft,elevationRight`, then one row per mouse. `SaveSpoutPositions` saves the mouse's row: the rig's controller sends it before every Go (20 Nov 2026), and the operator can send it at any time, such as after a move during the run. It writes a temporary file that then takes the list's place, so the other rows are kept and the list is never half written; a mouse without a row gets `lickSpoutDefaultPosition`. The positions the stage starts from and each save are rows of the experiment's events (`SpoutPositionsLoaded`, `SpoutPositionsDefault`, `SpoutPositionsSaved`; `Detail` the mouse and each axis's steps), and the manifest records the starting five numbers (`spout_positions`).

---

## Bonsai workflow

:::workflow
![LickSpoutStage](~/assets/workflows/devices/LickSpoutStage.svg){data-bonsai="~/src/OpenRigs.Devices/LickSpoutStage.bonsai"}
:::

Opens `StepperDriver-A` and `StepperDriver-B` behind their `.Data` and `.Commands` subjects and logs both once the experiment folder exists and the Harp clock is set. Each axis is on the motor `rig.yml`'s `stepperDriver` `wiring` names (`BaseTransverse`, `Radial` and `Elevation` with their `side`, each homed on the input of the same number, such as `Input1: { device: RadialHome, side: left }`); a motor wired to anything else is left alone, free for another device. Sets both drivers up and homes the five axis motors together, each within 60 s, then gives each motor its wiring's `maxSteps`, if any, as the driver's limit: the driver moves it no further from home, and the stage holds every target and saved position to it too; a motor wired with `reversed: true` has its axis's two nudges swapped, for a motor and home switch mounted at the other end; once done it publishes `LickSpoutStage.Homed`, announces `SpoutsHomed` into `ReadyStages` and moves the spouts in. It then moves for the controller's spout commands (`SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SpoutsDefault` (the in position reset to `lickSpoutDefaultPosition`, kept for the mouse only by `SaveSpoutPositions`), `SaveSpoutPositions`, `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftIn`, `LeftOut`, `RightIn`, `RightOut`, `BaseLeft`, `BaseRight`, each declared by the piece itself, `DeclareControls`, never by the workflow), a nudge by the experiment parameter `SpoutStepSize`, which the piece declares too and the stage takes live as the controller sets it (150 steps until then, shown on the controller one second after Start), and for `LickSpoutCommand` values a task sends to `LickSpoutStage.Commands`. Each target is published on `LickSpoutStage.Target`. Stops the workflow at Start, naming the entries `rig.yml` has, if it lacks `a` or `b`, and naming what is wrong if their wiring does not give each axis one motor with its home switch.

## Bias offset

A task's bias correction (`spouts.biasCorrection`, OpenRigs.Trials `SpoutBias`) moves the spouts sideways, on the base transverse axis alone, by an offset kept apart from the mouse's in position: every target but home (in, out, left only, right only) is the in position with the offset added to `baseTransverse`, then held to the axis's `maxSteps`, so the out position follows the corrected in position. `SaveSpoutPositions` saves the person's in position, never the offset, and a person's nudges still move the in position, the offset riding on top. The offset is 0 at Start; only `BiasReset` sets it back to 0. `SpoutsDefault` resets the in position and keeps the offset, so the stage's offset stays the one the task's `SpoutBias` counts (`OffsetSteps`, its trial column and its limit).

The task sends `BiasLeft`, `BiasRight` or `BiasReset` to `LickSpoutStage.Commands` (OpenRigs.Trials `SpoutCommands` times them) and, once, a `SpoutBiasSettings` (`StepSteps`, `MaxSteps`, from `SpoutBias.StageSettings`) to the piece's `LickSpoutStage.BiasSettings`, a `BehaviorSubject`. `BiasLeft` moves the offset one `StepSteps` toward the mouse's left, the way `BaseLeft` moves the base (the right spout toward the midline), `BiasRight` toward its right, `BiasReset` to 0; the stage holds the offset within `MaxSteps` either way and moves at once to the current placement's target (home stays home). Until settings come, a bias move does nothing. The offset goes through the same `reversed: true` flip as `BaseLeft` and `BaseRight`, so left is the mouse's left on every rig whose `rig.yml` says `reversed` correctly; it is logged the mouse's way. Each change is a `SpoutBiasMoved` row of the experiment's events, `Detail` the mouse and the new offset in steps, positive toward the mouse's left (`M1 biasSteps=-200`).

The setup, homing, moves and positions list are C# in OpenRigs.Devices (`LickSpoutWiring`, `SetUpLickSpoutDriver`, `HomeLickSpoutDriver`, `MoveLickSpoutDriver`, `ToLickSpoutCommand`, `SpoutPositions`, `LickSpoutStage`, `SpoutBiasSettings`), each with tests.
