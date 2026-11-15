# Experiment Schemas

> **Lab workflows differ.** This page teaches the generic open-rigs template, where a project has its own rig model in `rig.py` and its own `examples\rig.py`. A lab workflow has neither: every lab rig is `open_rigs.rigs.lab_rig.LabRig`, read from the machine's `rig.yml` (camelCase keys such as `behaviorBoard`, `timestampGeneratorGen3`, `licketySplit` and `camera`, each device kind a group of named entries; `open_rigs\rigs\rig-template.yml` is the whole file), and its devices are the pieces in [Building Blocks](../../articles/building-blocks.md). A new lab device is a new part of `LabRig`, not a field of a project's rig.

### Approach
Under the open-rigs framework, we aim to have clear separation between our experiment **parameters** and **implementation**.

1. Parameters are defined by [pydantic](https://docs.pydantic.dev/latest/) schemas which allow us to model and validate the inputs to our experiment.
2. Bonsai loads these parameters and acts as the **engine** of our experiment, taking care of interfacing with hardware and running the task logic in real time.

The end goal of this separation is to allow us to flexibly change experiment parameters: trial structure; hardware settings; stimulus definition etc., without changing between Bonsai workflows or manually editing parameters in the Bonsai editor.

### Schemas
Within the open-rigs schemas, we also aim to separate by:
1. Session - parameters like subject I.D., git repo status
2. Rig - hardware parameters like COM port, screen calibrations
3. Task - parameters controlling the task logic, e.g. inter-trial interval times, stimulus definitions

One aim of this separation is to avoid redundant changes in schema files that would exist if we used a single schema for each experiment. For example, a common pattern would be to define a set of rig paramters (for the devices installed on a particular experimental rig) and task logic (for a given experiment design) and then make multiple session files e.g. for a set of subjects to be run on the rig/experiment. The separation here allows us to have two 'static' files for rig and task logic and a few lightweight session files to switch between subjects.

### Rig Schema
> [!Note]
> 
> In this tutorial I have initiated the project with the name `reaction-time`. You will therefore see this name in multiple places in the code as it has been automatically inserted by the copier template, this will be different in your code depending what you have named your project, so be wary of directly copy-pasting from this tutorial as you may need to change references to the project name in some places.

In the project folder we created from the template, a Python module named for your project should have been created under `src\<your_project_name>` containing skeleton schema definition files `rig.py` and `task.py`. Let's first modify `rig.py` to define our rig schema:

```
from typing import Literal
from pydantic import Field

from open_rigs.core.rig import Rig
from open_rigs.devices.harp import HarpHobgoblin
from open_rigs.vision import Screen

from open_rigs_reaction_time import __semver__


class OpenRigsReactionTimeRig(Rig):
    version: Literal[__semver__] = __semver__
    harp_hobgoblin: HarpHobgoblin = Field(description="Harp Hobgoblin device")
    screen: Screen = Field(description="The main display for visual stimuli")
```

All we did here is to import some pre-defined definitions from the `open_rigs` package, and add them as members of the main rig class. The `open_rigs` package defines a number of reusable device definitions so that for common devices we don't need to rewrite their definitions for each experiment, and they are consistent across experiments. For the first iteration of this experiment this will be sufficient, later we will see how to implement custom device definitions for devices that are not defined in the main `open_rigs` schemas (e.g. Arduinos specific to a single rig or experiment).

### Task Schema
Next we'll write our task schema with the parameters required for the task logic in the experiment design. We'll start very simple, initially our visual stimuli will be drifting gratings that vary only in temporal frequency. For each stimulus we also need an associated target reaction delay time. Inside `src\<your_project_name>\task.py` let's add a class to represent a stimulus:
```
class Trial(BaseSchema):
    temporal_frequency: float = Field(ge=0, description="Temporal frequency of the gratings in this stimulus")
    target_delay: float = Field(ge=0, description="Target response time delay for the subject after this stimulus is presented")
```
Note above another use of pydantic that will be useful later, for our `temporal_frequency` and `target_delay` fields we can set validation options, in this case `ge=0` which means that these fields must have values >=0. For `target_delay` in particular, a negative value doesn't make sense in the context of this experiment (would require subject responding before the onset of the stimulus).

Now that we have a trial class, we can add the rest of the definition to the task parameters class. We'll add a list of trials to define the trial sequence, a 'timeout' field that defines the maximum response time, and another time value to determine the onset of presentation / inter-trial-interval; as well as an optional random number generator seed. Altogether `task.py` now looks like:
```
from typing import Literal, List, Optional
from pydantic import Field

from swc.aeon.schema import BaseSchema

from open_rigs_reaction_time import __semver__

class Trial(BaseSchema):
    temporal_frequency: float = Field(ge=0, description="Temporal frequency of the gratings in this stimulus")
    target_delay: float = Field(ge=0, description="Target response time (seconds) delay for the subject after this stimulus is presented")


class OpenRigsReactionTimeTaskParameters(BaseSchema):
    trials: List[Trial] = Field(description="The sequence of trials that will be delivered in the experiment")
    max_trial_time: float = Field(description="The maximum amount of time (seconds) allowed for a response in any trial. Exceeding this time should result in the trial aborting and moving to the next trial in the sequence")
    initial_delay_time: float = Field(description="Time (in seconds) between initiation of a new trial and onset of presentation of the trial stimulus")
    rng_seed: Optional[float] = Field(default=None, description="Seed of the random number generator for these task parameters")


class OpenRigsReactionTimeTaskLogic(BaseSchema):
    version: Literal[__semver__] = __semver__
    name: Literal["OpenRigsReactionTime"] = Field(default="OpenRigsReactionTime", description="Name of the task logic", frozen=True)
    task_parameters: OpenRigsReactionTimeTaskParameters = Field(description="Parameters of the task logic")
```

### Generation
We now have a complete schematic description of our small experiment example. We will expand on this later, but for now we can start on the Bonsai implementation that will actually run the experiment using these parameters.

The first step is to compile our separate pydantic schemas into a single unified schema, and from this generate C# classes that can be parsed by Bonsai. The `open-rigs` template provides a method for doing the with the `regenerate.py` script. This can be run with:
```
uv run .\src\open_rigs_implementation_example\regenerate.py
```

After this has run, you should see two new files created:
1. `src\DataSchemas\open_rigs_implementation_example.json` - the combined pydantic schemas dumped into a single .json schema
2. `src\Extensions\OpenRigsImplementationExample.Generated.cs` - equivalent C# classes representing the schema components, as a Bonsai compatible extension.
