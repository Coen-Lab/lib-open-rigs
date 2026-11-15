# Adding a New Hardware Module

> **Lab workflows differ.** This page teaches the generic open-rigs template, where a project has its own rig model in `rig.py` and its own `examples\rig.py`. A lab workflow has neither: every lab rig is `open_rigs.rigs.lab_rig.LabRig`, read from the machine's `rig.yml` (camelCase keys such as `behaviorBoard`, `timestampGeneratorGen3`, `licketySplit` and `camera`, each device kind a group of named entries; `open_rigs\rigs\rig-template.yml` is the whole file), and its devices are the pieces in [Building Blocks](../articles/building-blocks.md). A new lab device is a new part of `LabRig`, not a field of a project's rig.

This tutorial walks through adding a new hardware module to a open-rigs experiment. Modules will typically be built locally inside your experiment repository first, then migrated to the shared `acquisition` package once they are tested and stable. This tutorial covers the local build only — migration to `acquisition` will be covered in a separate guide.

A hardware module has two complementary parts:

1. **A Bonsai workflow** (a `.bonsai` file in your experiment's `src/Extensions` folder) — implements the hardware control.
2. **A Python schema**. This can be written in Python anywhere but should be synthesized by your rig module (in `src/open_rigs_my_new_project/rig.py`). This defines the device's configuration contract.

In this tutorial we will build a simple example: a custom serial device that reads lines from a sensor over a COM port.

---

## Design principles

Before starting to build a module, keep the following principles in mind.

### Subjects are the module's API

Any data you want to log, or access from elsewhere in your workflow, **must be passed to a named Subject**. If it is not on a Subject, it is invisible to the rest of the workflow. Think of your module's Subjects as its public interface:

- **Outputs** (data flowing out of the device) → use `PublishSubject` or `BehaviorSubject`
- **Inputs** (commands flowing into the device) → use `PublishSubject`

Name Subjects clearly and descriptively (e.g. `SensorReadings`, `PumpCommands`). Subject names should also be **externalized properties** so that the name can be overridden from the rig configuration — this is what allows multiple instances of the same module to coexist without name collisions.

### Think about output frequency

Not everything needs to be emitted on every sample. Before publishing data on a Subject, consider:

- What downstream systems will consume this data?
- What will you log and what will you synchronize that with?
- Is every sample useful, or only state changes?
- Will a high-frequency stream overwhelm logging or downstream operators?

For example, a running wheel encoder might emit thousands of counts per second — you may only want to publish velocity calculated over a rolling window, not raw counts.

### Use Harp timestamps everywhere, consistently

All data should be timestamped. Where a Harp device is present on the rig, **always use Harp timestamps**. Never mix Harp and system (Reactive `Timestamp`) timestamps within the same data streams and avoid doing so across data streams. Doing so breaks downstream alignment and makes data analysis significantly harder.

If your module does not directly produce Harp timestamps, subscribe to your Harp clock synchronizer subject and use `WithLatestFrom` to attach the most recent Harp timestamp to your data before publishing it.

---

## Part 1: Build the Bonsai workflow

In most cases you will build the device module as a group inside an existing experimental workflow rather than starting from a blank file. Once it is working you can extract it as a reusable extension.

1. Inside your main workflow, add the nodes for your device and test them with properties set directly in the Bonsai editor.
2. Once satisfied, select all the nodes that make up the device, right-click and choose **Group** to wrap them in a nested workflow.
3. Right-click the group node and select **Save as workflow...** — this saves it as a `.bonsai` file into your project's `Extensions` folder, making it available as a reusable sub-workflow across the project.

A good convention is to name the file after the device, e.g. `MySensor.bonsai`.

### Workflow structure

A typical device workflow has three responsibilities:

1. **Initialize the device** — open the connection using the configuration properties
2. **Expose outputs** — publish device data on named Subjects
3. **Expose inputs** — subscribe to command Subjects and forward them to the device

### Externalize all configuration properties

Any property you want to be settable from the rig configuration needs an `ExternalizedMapping` node. This is what allows Bonsai to inject values from the rig JSON file at startup rather than having them hardcoded in the workflow.

Use `PascalCase` for `DisplayName` values — this is the naming convention used in the rig JSON. Keep a note of the names you choose, as the Python schema fields you define in Part 2 will need to correspond to them.

### Externalize Subject names

Subject names must also be externalized so they can be set from the rig configuration. This allows the same workflow to be used for multiple device instances without Subject name collisions. Use the `Category="Subjects"` attribute to group these separately in the Bonsai property grid — set `DisplayName` to something descriptive like `SensorReadingsSubjectName` so it is clear in the property panel what each subject name controls.

### Example workflow

Below is a complete minimal example for a serial sensor that reads lines and publishes them, and accepts command strings from a Subject:

:::workflow
![MySensor](~/workflows/MySensor.bonsai)
:::

### Save as a local extension

Group the Bonsai module into a `GroupWorkflow`. Right click and select **Save as workflow...**. The file will appear in the `Extensions` folder of your project and become available as a node in the Bonsai editor's toolbox. Press the **Reload Extensions** button the restart Bonsai and check everything compiles correctly.

---

## Part 2: Define the Python schema

With the workflow tested and working, the next step is to define a Python schema that matches the externalized properties. The schema declares what configuration parameters the device requires — this is what gets serialized to the rig JSON file and loaded by Bonsai at startup.

### Choose a base class

| Base class | Use when |
|---|---|
| `Device` | Generic device with no standard protocol |
| `SerialDevice` | Device communicating over a serial port |
| `HarpDevice` | Device implementing the Harp protocol |

All base classes are in `open_rigs.rigs` (`open_rigs` package).

### Match field names to your externalized properties

Each Python field name must correspond to the `DisplayName` you used in Bonsai — the schema serializes to camelCase JSON, which is what Bonsai reads at startup:

| ExternalizedMapping DisplayName | Python schema field |
|---|---|
| `PortName` | `port_name` |
| `BaudRate` | `baud_rate` |
| `SamplingIntervalMs` | `sampling_interval_ms` |

### Write the schema

Add your device class to `src/<your_project>/rig.py` (or to `open_rigs/src/open_rigs/rigs/device.py` if the device will be shared across experiments):

```python
from typing import Literal
from pydantic import Field
from open_rigs.rigs.device import SerialDevice

class MySensor(SerialDevice):
    device_type: Literal["MySensor"] = "MySensor"
    sampling_interval_ms: int = Field(
        default=10,
        ge=1,
        description="Interval in milliseconds between sensor readings."
    )
```

`SerialDevice` already provides `port_name` and `baud_rate`, so only add fields that are specific to your device. Use hardware-native types (`Byte`, `UShort`, `Int` from `open_rigs.rigs.data_types`) for any fields that map directly to hardware registers — they carry built-in range validation.

### Register the device on the rig

Add the device to your rig schema class:

```python
from typing import Dict
from open_rigs.rigs.base import BaseSchema
from .rig import MySensor  # or from open_rigs.rigs.device import MySensor

class MyProjectRig(BaseSchema):
    my_sensor: Dict[str, MySensor] = Field(
        description="Mapping from logical name to MySensor devices."
    )
```

Using a `Dict[str, ...]` rather than a single instance allows multiple sensors to be registered under different logical names (e.g. `"left"`, `"right"`).

---

## Part 3: Create a rig configuration

Never write or edit the rig JSON by hand. The Pydantic models are the ground truth for the schema — any manual edits to the JSON will be overwritten the next time you run `examples/rig.py`. Instead, instantiate your schema in Python, populate the fields, and serialize it to JSON. The template generates an `examples/rig.py` script as a starting point for this.

Add your new device to the rig instance in `examples/rig.py`:

```python
from my_project.rig import MyProjectRig, MySensor

rig = MyProjectRig(
    my_sensor={
        "primary": MySensor(
            port_name="COM6",
            baud_rate=115200,
            sampling_interval_ms=10,
        )
    }
)

def main(path_seed: str = "./local/{schema}.json"):
    import os
    os.makedirs(os.path.dirname(path_seed), exist_ok=True)
    with open(path_seed.format(schema=rig.__class__.__name__), "w", encoding="utf-8") as f:
        f.write(rig.model_dump_json(indent=2, by_alias=True))

if __name__ == "__main__":
    main()
```

Run it to produce the JSON file that Bonsai will load:

```
uv run examples/rig.py
```

This writes `local/MyProjectRig.json`. The key `"primary"` is the logical name for this device instance — add further entries to the dictionary for additional instances.

After adding a new device to the rig schema, re-run `regenerate.py` to rebuild the C# classes so Bonsai can deserialize the updated schema:

```
uv run src/<your_project>/regenerate.py
```

---

## Next steps

Once the module is working reliably in your experiment repository, it can be contributed back to the shared `acquisition` package so that other experiments can use it without duplicating the workflow. See the migration guide (coming soon) for how to do this.

For devices that are broadly applicable across the open-rigs platform, the Python schema should also be contributed to `open_rigs`. Raise an issue in the [roadmap repository](https://github.com/open-rigs/roadmap) to propose adding it.
