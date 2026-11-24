# LicketySplit

The LicketySplit (`who_am_i = 1400`) is a Harp lick detector. It samples one or more capacitive/ADC channels and emits trigger events when the signal crosses configurable thresholds, providing low-latency lick timestamps on the Harp clock.

---

## Python schema

`LicketySplit` is defined in `open_rigs.devices` and extends `HarpDevice`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `port_name` | `str` | - | Serial port the device is connected to (e.g. `COM3`) |
| `channel0_trigger_threshold` | `Byte` or none | none | ADC threshold (0-255) above which Channel 0 triggers a lick, written at start; left out, the board keeps its own |
| `channel0_untrigger_threshold` | `Byte` or none | none | ADC threshold (0-255) below which Channel 0 untriggers a lick, written at start; left out, the board keeps its own |

Leave both thresholds out to keep the board's own: writing 0/0 hangs the board about 1 s after start (no heartbeats until a power cycle). Set them only from a measurement, a reading at rest and one during a touch, with the trigger between them and the untrigger below the trigger. The rig model refuses a trigger of 0, a trigger not above the untrigger, and an untrigger without a trigger. The register dump at start records the thresholds the board used.

### Configuration example

In `rig.yml`, under `licketySplit`, one entry per detector (`left` is `LicketySplit-Left`), read by the lab rig model (`open_rigs.rigs.lab_rig.LabRig`):

```yaml
licketySplit:
  left:
    portName: COM5
    usbSerial: CHANGE-ME
  right:
    portName: COM6
    usbSerial: CHANGE-ME
    channel0TriggerThreshold: 120   # only from a measurement, as above
    channel0UntriggerThreshold: 60
```

In Python it is `LabRig(lickety_split={"left": LicketySplit(port_name="COM5", ...)})`.

---

## Bonsai workflow

:::workflow
![LicketySplit](~/assets/workflows/devices/LicketySplit.svg){data-bonsai="~/src/OpenRigs.Devices/LicketySplit.bonsai"}
:::

`LicketySplit` is a device piece (see [Building Blocks](../../articles/building-blocks.md)): `Name` (`LicketySplit-<Entry>`) picks its entry under `licketySplit` in `rig.yml`, published as the `AsyncSubject` `<Name>`. It opens the device on its `portName` behind `<Name>.Data` and `<Name>.Commands`, writes each channel 0 threshold its entry sets at its first message (none it leaves out, so the board keeps its own), publishes the lick state (true while a lick is detected) on `LickStateSubjectName`, and logs the device as `<Name>` once the experiment folder exists and the Harp clock is set. Stops the workflow at Start, naming the entries `rig.yml` has, if it has none by that name.

### A board that doesn't answer

The board's firmware, the Harp Pico core, reads commands with no resync or timeout. A command cut short, such as a write cut off as a workflow stops, can leave it waiting for the rest of a frame of up to 255 bytes, reading every later command as part of it. The board still sends heartbeats, but never answers WhoAmI, so the device never opens and the workflow waits at ReadyGate, across any number of restarts.

Before the device opens, `FlushPicoHarp` asks the board WhoAmI. A board that answers is left alone. A silent one gets 320 zero bytes, then 5 twice: they complete the pending frame and are message type 0, which the core ignores. If the board then answers, standard error gets `FlushPicoHarp: flushed LicketySplit-Left (COM7)`; if not, the workflow stops with "does not answer WhoAmI even after a flush: power-cycle it". Power-cycling also clears a stuck board by hand.
