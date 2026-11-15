## New Project

> **Lab workflows differ.** This page teaches the generic open-rigs template, where a project has its own rig model in `rig.py` and its own `examples\rig.py`. A lab workflow has neither: every lab rig is `open_rigs.rigs.lab_rig.LabRig`, read from the machine's `rig.yml` (camelCase keys such as `behaviorBoard`, `timestampGeneratorGen3`, `licketySplit` and `camera`, each device kind a group of named entries; `open_rigs\rigs\rig-template.yml` is the whole file), and its devices are the pieces in [Building Blocks](../articles/building-blocks.md). A new lab device is a new part of `LabRig`, not a field of a project's rig.

In order for open-rigs projects to have a somewhat standardised structure, a [copier](https://copier.readthedocs.io/en/stable/) template is [provided](https://github.com/open-rigs/rig-template) as part of the framework. 

First, install the `copier` tool from the command line (should only need to be done once per machine):

```
uv tool install copier
```

Next, run `copier` with the open-rigs [rig template](https://github.com/open-rigs/rig-template) in the directory where you want to create your new project:

```
copier copy --vcs-ref main https://github.com/open-rigs/rig-template.git ./my-new-project
```
`copier` will prompt you for some basic template variables and then generate required files and directory structure.

### Deploy Project
The open-rigs template comes with a script to 'bootstrap' the Python and Bonsai development environments, run this with:
```
.\scripts\deploy.ps1
```

## Create a Schema
Schemas are `pydantic` models of the parameters that control an experiment. They define the data types of an experiment **in abstract**, e.g. a USB device may have a `port_name` property expected to have type `string`. The open-rigs template provides skeletons for the `task` and `rig` schemas. 

### Rig
To define a rig that contains a single Arduino device that writes data over serial, in `rig.py`:

```
from typing import Literal
from pydantic import Field

from open_rigs.rigs.base import BaseSchema
from open_rigs.rigs.device import SerialDevice

from <your_project_name> import __semver__


class <YourProjectName>Rig(BaseSchema):
    version: Literal[__semver__] = __semver__
    arduino: SerialDevice = Field(description="Arduino with serial write data")
```

### Task
Similarly in `task.py`, define some parameters that control task logic:

```
# Import core types
from typing import Literal
from pydantic import Field

from swc.aeon.io import reader
from swc.aeon.schema import BaseSchema, data_reader

from <your_project_name> import __semver__


class <YourProjectName>TaskParameters(BaseSchema):
    inter_trial_interval: float = Field(description="The time between trials in seconds")
    n_trials: int = Field(description="The total number of trials to run")


class <YourProjectName>TaskLogic(BaseSchema):
    version: Literal[__semver__] = __semver__
    name: Literal["<YourProjectName>"] = Field(default="<YourProjectName>", description="Name of the task logic", frozen=True)
    task_parameters: <YourProjectName>TaskParameters = Field(description="Parameters of the task logic")
```

### Generating the Interface
After writing our experiment schemas in a pydantic model, we need to compile the models together into a `.json` schema description, and use [`sgen`](https://github.com/bonsai-rx/sgen) to automatically write corresponding C# classes that can be used in Bonsai. The template provides a script `regenerate.py` that takes care of these steps:

```
uv run .\src\<your-project-name>\regenerate.py
```

### Initialize Git Tracking
It is good practice to track changes to the project with [Git](https://git-scm.com/), and to maintain a remote repository of those changes with [GitHub](https://github.com/).

Navigate into your newly created project folder and initialize a local git repository:
```
cd my-new-project
git init -b main
```

Make an initial commit:
```
git add .
git commit -m "Deploy new open-rigs rig"
```

## Creating Configurations
We can use our schemas to now generate configuration `.json` files that will be loaded by Bonsai to run our experiment. The template provides 3 example scripts to generate these configurations in the `examples` folder. 

### Session
`session.py` holds metadata about an experiment session not specific to a given rig or task, including subject id and git repository state. An example valid `session.py` could look like:

```
import datetime
import os
import git

from open_rigs.rigs.experiment import Experiment

session = Experiment(
    subject_id="Plimbo",
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

Here we mostly populate fields directly with values (e.g. "Plimbo" for the `subject_id`). In other cases, we automate these fields as in the case of `commit` which uses the `git` library to automatically assign the current commit id of the repository. Geneate a settings file from this script with:

```
uv run .\examples\session.py
```

### Rig
Similarly, update the `rig.py` script in the `examples` folder to define the Arduino device used in this configuration:

```
import os

from open_rigs_implementation_example.rig import (
    <YourProjectName>ExampleRig
)

from open_rigs.rigs.device import SerialDevice

rig = <YourProjectName>ExampleRig(
    arduino=SerialDevice(port_name="COM4")
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

The main change here is to import the `SerialDevice` class, and set a concrete port parameter for the `arduino` field of the rig. Run this script to produce the rig configuration file:

```
uv run .\examples\rig.py
```

### Task
Repeating the process for the `task.py` script in the `examples` folder:

```
import os

from open_rigs_implementation_example.task import (
    <YourProjectName>TaskLogic,
    <YourProjetcName>TaskParameters,
)

task_logic = <YourProjetcName>TaskLogic(
    task_parameters=<YourProjetcName>TaskParameters(
        inter_trial_interval = 2.0
        n_trials = 5
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

Here we've just updated our task parameters with some concrete values for `inter_trial_interval` and `n_trials`. Run this script to produce the task configuration file:

```
uv run .\examples\task.py
```

## Bonsai Implementation

### Ingesting Configurations

To use the parameters we've defined in our configuration in Bonsai, we need to:

1. Load the files themselves
2. Deserialize them into accessible parameters

For example, for our rig configuration:

:::workflow
![IngestConfiguration](~/assets/workflows-static/IngestConfiguration.svg){data-bonsai="~/assets/workflows-static/IngestConfiguration.bonsai"}
:::

Because of the auto-generated code we generated with `regenerate.py`, Bonsai can deserialize a raw `.json` file into accessible data classes and properties which we can use to parameterise parts of the workflow.