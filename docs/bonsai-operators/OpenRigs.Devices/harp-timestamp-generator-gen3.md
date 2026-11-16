# Harp Timestamp Generator Gen3

The Harp Timestamp Generator Gen3 (`who_am_i = 1158`) is a hardware clock synchroniser for Harp networks. It broadcasts a shared hardware timestamp over audio jack cables, allowing all devices on the rig to record data against a common timebase rather than relying on the PC system clock.

---

## Python schema

`HarpTimestampGeneratorGen3` is defined in `open_rigs.devices` with the following fields:


| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `port_name` | `str` | - | Serial port the device is connected to (e.g. `COM3`) |
| `timer_frequency` | `TimerFrequency` | `Timer1000Hz` | Rate at which timestamp events are sent to Bonsai |

`TimerFrequency` is used to set the rate at which clock events are generated on the Harp device: `Disabled`, `Timer50Hz`, `Timer100Hz`, `Timer200Hz`, `Timer500Hz`, `Timer1000Hz`. These are exposed in Bonsai by a subject that can then be used as the timestamps for the rest of your workflow. 

### Configuration example

In `rig.yml`, under `timestampGeneratorGen3`, one entry per generator (`main` is `TimestampGeneratorGen3-Main`), read by the lab rig model (`open_rigs.rigs.lab_rig.LabRig`):

```yaml
timestampGeneratorGen3:
  main:
    portName: COM3
    usbSerial: CHANGE-ME
    timerFrequency: Timer1000Hz
```

In Python it is `LabRig(timestamp_generator_gen3={"main": HarpTimestampGeneratorGen3(port_name="COM3", timer_frequency=TimerFrequency.Timer1000Hz)})`.

---

## Bonsai workflow

:::workflow
![ClockFromTimestampGenerator](~/assets/workflows/devices/ClockFromTimestampGenerator.svg){data-bonsai="~/src/OpenRigs.Devices/ClockFromTimestampGenerator.bonsai"}
:::

`ClockFromTimestampGenerator` is the rig's clock from the generator, one of the two clocks a workflow can have (the other is the Behavior board's own, [`ClockFromBoard`](behavior-board.md#clockfromboard)); a workflow has exactly one. Its `Name` is `TimestampGeneratorGen3-<Entry>`, its entry under `timestampGeneratorGen3` in `rig.yml`. It opens the generator on its `portName` behind `<Name>.Data` and `<Name>.Commands` and logs it as `<Name>`. It sets the generator's clock to the PC's UTC time and re-sends the write at each heartbeat until one agrees within 2 s (`SetHarpClock`), and writes `rig.yml`'s `timerFrequency`. It publishes `Timestamp` (the generator's timer, in Harp seconds) and `HarpClockSet` (the first `Timestamp` within 2 s of UTC), and announces `HarpClockSet` into `ReadyStages`. `HarpClockSet` itself is declared by `ReadyGate`, which comes before every device, as each device's log waits for it. The workflow stops, naming the generator, if its clock is not set within 10 s of its first heartbeat, it sends no heartbeat within 30 s, or its heartbeats stop for 3 s.

---

## Using the timebase in other modules

To stamp data from a non-Harp device with the hardware clock, subscribe to `Timestamp`, pair it with your data stream using `WithLatestFrom`, then pass the result to `CreateTimestamped`:

> **Note:** Unlike native Harp devices, this timestamp is assigned in software when the event arrives on the PC, not in hardware at the moment of acquisition. It is therefore subject to PC scheduling jitter and USB latency. For applications requiring sub-millisecond timing accuracy, use a Harp device directly.

:::workflow
![UsingTimebase](~/workflows/UsingTimebase.bonsai)
:::
