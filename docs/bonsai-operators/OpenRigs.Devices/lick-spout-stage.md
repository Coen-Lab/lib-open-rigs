# Lick Spout Stage

The lick spout stage positions a pair of lick spouts in front of the animal on five stepper axes, driven by two Harp StepperDriver boards: board `a` drives the base transverse, left radial and right radial axes, board `b` the left and right elevations. `LickSpoutStage` is its device piece (see [Building Blocks](../../articles/building-blocks.md)).

---

## rig.yml

```yaml
stepperDriver:
  a: { portName: COM3, usbSerial: CHANGE-ME }
  b: { portName: COM4, usbSerial: CHANGE-ME }
lickSpoutDefaultPosition: { baseTransverse: 0, leftRadial: 0, rightRadial: 0, leftElevation: 0, rightElevation: 0 }
```

A mouse's own positions are `calibration\LickSpoutPositions\<SubjectId>.csv` beside `rig.yml`, one line: `baseTransverse,leftRadial,rightRadial,leftElevation,rightElevation`. The operator saves them with `SaveSpoutPositions`; a mouse without a file gets `lickSpoutDefaultPosition`.

---

## Bonsai workflow

:::workflow
![LickSpoutStage](~/assets/workflows/devices/LickSpoutStage.svg){data-bonsai="~/src/OpenRigs.Devices/LickSpoutStage.bonsai"}
:::

Opens `StepperDriver-A` and `StepperDriver-B` behind their `.Data` and `.Commands` subjects and logs both once the experiment folder exists and the Harp clock is set. Sets both drivers up and homes all five motors together, each within 60 s; once done it publishes `LickSpoutStage.Homed`, announces `SpoutsHomed` into `ReadyStages` and moves the spouts in. It then moves for the controller's spout commands (`SpoutsHome`, `SpoutsIn`, `SpoutsOut`, `SaveSpoutPositions`, `LeftUp`, `LeftDown`, `RightUp`, `RightDown`, `LeftFront`, `LeftBack`, `RightFront`, `RightBack`, `BaseLeft`, `BaseRight`, each declared by the piece itself, `DeclareControls`, never by the workflow), a nudge by the experiment parameter `SpoutStepSize`, which the piece declares too and the stage takes live as the controller sets it (150 steps until then, shown on the controller one second after Start), and for `LickSpoutCommand` values a task sends to `LickSpoutStage.Commands`. Each target is published on `LickSpoutStage.Target`. Stops the workflow at Start, naming the entries `rig.yml` has, if it lacks `a` or `b`.

The setup, homing, moves and positions file are C# in OpenRigs.Devices (`SetUpLickSpoutDriver`, `HomeLickSpoutMotor`, `MoveLickSpoutDriver`, `ToLickSpoutCommand`, `SpoutPositions`, `LickSpoutStage`), each with tests.
