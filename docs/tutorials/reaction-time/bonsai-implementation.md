# Bonsai Implementation

> **Lab workflows differ.** This page teaches the generic open-rigs template, where a project has its own rig model in `rig.py` and its own `examples\rig.py`. A lab workflow has neither: every lab rig is `open_rigs.rigs.lab_rig.LabRig`, read from the machine's `rig.yml` (camelCase keys such as `behaviorBoard`, `timestampGeneratorGen3`, `licketySplit` and `camera`, each device kind a group of named entries; `open_rigs\rigs\rig-template.yml` is the whole file), and its devices are the pieces in [Building Blocks](../../articles/building-blocks.md). A new lab device is a new part of `LabRig`, not a field of a project's rig.

### Creating Configurations
The experiment schemas we have created already define the available parameters to set to control our experiment, next we need to create concrete definitions for these parameters that will be loaded into Bonsai. The `open-rigs` template has example scripts for generating these definitions in the `examples` folder.

### Session
In `experiment\session.py` we can see the script for generating the session metadata. When generated fresh from the template it should have some error warnings as we have not filled in the required fields. Fill these in for an example session:
```
import os

import git
from open_rigs.core.experiment import ExperimentSession

session = ExperimentSession(
    subject_id="Plimbo",
    session_id="001",
    workflow="main.bonsai",
    commit=git.Repo(search_parent_directories=True).head.object.hexsha,
    repository_url=""
)

def main(path_seed: str = "./local/{schema}.json"):
    os.makedirs(os.path.dirname(path_seed), exist_ok=True)
    models = [session]

    for model in models:
        with open(path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2, by_alias=True))


if __name__ == "__main__":
    main()
```
I've left the `repository_url` blank, this can be filled in with the correct url if you have a remote repository already set up. I also use the git library to automatically populate the commit hash in this example. Run the script with:
```
uv run examples\session.py
```
You should see a file `Experiment.json` show up in the `local` folder. This is the actual session settings file that will be loaded and deserialized by Bonsai.

### Rig
We'll repeat the process for the rig configuration in `examples\rig.py`:
```
import os

from open_rigs.core import Vector3
from open_rigs.devices.harp import HarpHobgoblin
from open_rigs.vision import DisplayCalibration, DisplayExtrinsics, DisplayIntrinsics, Screen, ViewportConfiguration

from open_rigs_reaction_time.rig import OpenRigsReactionTimeRig

rig = OpenRigsReactionTimeRig(
    root_path="../temp_data",
    harp_hobgoblin=HarpHobgoblin(port_name="COM7"),
    screen=Screen(
        window_width=1000,
        window_height=1000,
        calibration={
            "main": DisplayCalibration(
                extrinsics=DisplayExtrinsics(
                    rotation=Vector3(x=0.0, y=0.0, z=0.0),
                    translation=Vector3(x=0.0, y=1.309016, z=-13.27)
                ),
                intrinsics=DisplayIntrinsics(
                    viewport_configuration=ViewportConfiguration(),
                    display_width=20,
                    display_height=15
                )
            )
        }
    )
)

def main(path_seed: str = "./local/{schema}.json"):
    os.makedirs(os.path.dirname(path_seed), exist_ok=True)
    models = [rig]

    for model in models:
        with open(path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2, by_alias=True))


if __name__ == "__main__":
    main()
```

Here we import the `HarpHobgoblin` schema from `open_rigs` and create an instance inside the rig definition with the USB connection COM port for our machine. The `Screen` settings work slightly differently than other parameters in open-rigs schemas. Because a visual environment can be defined by one to many separate viewports, the calibration for the screen is defined as a dictionary of named parameter sets. In our case we only want to define 1 viewport that covers the entire screen so we create a single entry called `"main"` with some `DisplayExtrinsics` that correspond to the view position, and some `DisplayIntrinsics` that correspond to the height and width of the display as well as the viewport size on screen this will be drawn to. Run this script with:
```
uv run examples\rig.py
```

### Task
Finally, we'll define an example experiment in `task.py`:

```
import os

from open_rigs_reaction_time.task import (
    OpenRigsReactionTimeTaskLogic,
    OpenRigsReactionTimeTaskParameters,
    Trial
)

task_logic = OpenRigsReactionTimeTaskLogic(
    task_parameters=OpenRigsReactionTimeTaskParameters(
        max_trial_time=60,
        initial_delay_time=5,
        trials=[
            Trial(temporal_frequency=1, target_delay=1),
            Trial(temporal_frequency=2, target_delay=2),
            Trial(temporal_frequency=1, target_delay=1),
            Trial(temporal_frequency=2, target_delay=2),
            Trial(temporal_frequency=1, target_delay=1)
        ]
    ),
)

def main(path_seed: str = "./local/{schema}.json"):
    example_task_logic = task_logic
    os.makedirs(os.path.dirname(path_seed), exist_ok=True)
    models = [example_task_logic]

    for model in models:
        with open(path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2, by_alias=True))


if __name__ == "__main__":
    main()
```

We set a global maximum trial time of 60s and an initial delay of 5s, and then our sequence of `trial`. Run this script with:
```
uv run examples\task.py
```

You should now have 3 `.json` files in the `local` folder that describe a full instance of the reaction time experiment. These are the files that will be loaded by bonsai to manage experiment parameters.

### Bonsai Workflow

Below is the bonsai workflow implementation written in the project's `main.bonsai` file.

:::workflow
![Reaction Time Workflow](./project/src/main.bonsai)
:::

Opening this workflow in the editor, you may notice that there are only 3 configurable properties in the bonsai editor property grid: `RigPath`, `SessionPath` and `TaskPath`. Theses should be assigned to the corresponding `.json` files we generated in the `local` folder. Bonsai will load these `.json` files and use them to populate all parameters of the experiment. Let's step through the components of this workflow.

#### Schema Reading / Loading

The `ReadSchemas` group deals with reading the `.json` settings files, exposing the raw text in a set of `AsyncSubjects`. The `LoadSchemas` group subscribes to these raw text subjects and deserializes them into a data object that can be used to update parameters in other parts of the workflow. When we run the `regenerate.py` script referenced prerviously, part of its job is to run a tool called [`sgen`](https://github.com/bonsai-rx/sgen) which is a code generation tool that takes schema files in the form of `.json` or `.yaml` and creates C# classes and utility methods usable by bonsai based on the data definitions in these schemas. You can see the result of this generation in `src\Extensions\OpenRigsReactiontime.Generated.cs`. One part of this generation is the creation of a deserialization operator for bonsai that takes raw text from our experiment definition files and converts it into bonsai data objects. If you right-click one of these operators in `LoadSchemas` and expand the output option you can see the mapping of the settings files to this data object.

#### Initialization

In the `Initialization` group we implement a very basic experiment control interface. and create an rng seed downstream of the rng seed parameter in our settings file for task logic. For experiment control, we simply create a subject to be used to trigger the start of the experiment, and tie this subject to initiation of a Harp read dump. This is a Harp hardware specific operation that forces the Harp device to report the current state of all its registers, so that we can log the initial state of the device when the experiment starts. For the rng seed implementation, we initialise a `CreateRandom` object with the seed defined in our input settings file.

#### Hardware

There are two main hardware components to set up for this experiment: 1) display screen, 2) Harp Hobgoblin which are implemented in the `Hardware` group. 

The `ScreenConfiguration` group creates the display window and loads `BonVision` resources, with parameters of the display window populated from the rig schema. The rig schema allows us to define multiple screen calibrations in a dictionary, which can be used to define multiple view windows and viewports within a bonsai visual environment. In this case we only have 1 view, but the interface between the rig schema and the `ScreenConfiguration` is the same, we multicast the dictionary of name `ScreenCalibration`s to a subject in `ScreenConfiguration` which then dynamically populates the specified views. Note here also that we use a custom conversion operator `ConvertCalibration` for convenience to avoid verbose bonsai code converting from our schema data object to the individual data types expected by `ScreenConfiguration`.

The `Hobgoblin` device contains a thin 'subject wrapper' around the bonsai operator that connects to the Harp device itself. Two subjects are exposed, one as a source of Harp commands to the device (`HobgoblinCommands`) and another as a source of Harp event (`HobgoblinEvents`). This mainly exists to allow for subscription to events from the Harp board or to send commands to the board from anywhere in the workflow. The group also exposes the `PortName` property which is populated by the rig schema. This group also defines a timestamp source subject `HarpTimestampSource` to be used as a global timing source across the workflow. This is created by filtering the Hobgoblin events to the analog data register, which provides a constant, periodic source of timestamps from the hardware.

#### Trial Logic

The main workhorse of the workflow is the `DoTrial` branch which contains a common pattern for bonsai trial-based experiments. Once the screen configuration is complete (signaled by the first output from `Draw3DStim`) we subscribe to the task logic parameters generated from the task logic settings and use `Merge` on the `Trials` property to create a sequence of each individual trial. For each trial a new higher-order observable is instantiated with a `CreateObservable` operator named `DoTrial`. Inside the `CreateObservable` the general logic for an individual trial is defined, along with an exit condition for the trial. Finally we use `Concat` on the `DoTrial` operator to ensure that trials are proceeded one-by-one in sequence.

The trial logic itself draws a grating with `DrawGrating`, the parameters of which are populated from the current `Trial` defined in the task logic schema. The `HarpTimestampSource` is used to timestamp stimulus onsets, and then compared against the Harp timestamp of a user-input event from the Hobgoblin. The difference between user reaction time and the target reaction time is calculated and displayed after the user-input event or after a trial timeout.

#### Logging

The `Logging` group contains implementation of logging data sources to disk as well as settng up the folder structure of the logging. This is primarily handled by packaged `open-rigs` components to maintain consistency across experiments. For example the `LogController` constructs the appropriate logging path format, while `LogHarpDevice` logs the output the Harp Hobgoblin in a Harp standard format.

### Running the Experiment

All these elements should now result in a working experiment when the bonsai workflow is run. The workflow will step through the trials we defined in `task.py` one-by-one and update the parameters for each trial (e.g. temporal frequency and target reaction time). 