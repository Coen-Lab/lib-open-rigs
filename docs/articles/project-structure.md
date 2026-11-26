# Project Structure

> **Lab workflows differ.** This page describes the generic open-rigs template, where a project has its own rig model (`rig.py`). A lab workflow has no `rig.py`: every lab rig is `open_rigs.rigs.lab_rig.LabRig`, read from the machine's `rig.yml`, and its devices are the pieces in [Building Blocks](building-blocks.md).

When you run the copier template you are asked for three inputs:

| Input | Example | Description |
|---|---|---|
| `project_name` | `my-experiment` | Kebab-case name for this experiment |
| `author_name` | `Jane Doe` | Your name |
| `prefix` | `open-rigs` | Lab or organisation prefix (kebab-case) |

From these inputs the template auto-generates a set of internal names used throughout the project:

| Variable | Example | Used for |
|---|---|---|
| `python_folder_name` | `open_rigs_my_experiment` | Python module directory and import name |
| `python_package_name` | `open-rigs-my-experiment` | `pyproject.toml` package name |
| `dotnet_name` | `MyExperiment` | .NET class name fragment |
| `dotnet_full_name` | `OpenRigs.MyExperiment` | .NET namespace |
| `python_class_prefix` | `OpenRigsMyExperiment` | Prefix for generated Python/C# schema classes |

The resulting project looks like this (using `my-experiment` as an example):

```
my-experiment/
├── .bonsai/                        # Bonsai environment
│   ├── Bonsai.config               # Package references
│   ├── NuGet.config                # NuGet feed configuration (includes local_packages/)
│   └── Setup.cmd / Setup.ps1       # Scripts to bootstrap the Bonsai environment
├── .config/
│   └── dotnet-tools.json           # Pinned dotnet tool versions (e.g. Bonsai.Sgen)
├── examples/                       # Example JSON parameter files
│   ├── rig.py                      # Example rig schema instantiation
│   ├── session.py                  # Example session schema instantiation
│   └── task.py                     # Example task schema instantiation
├── local_packages/                 # Local NuGet packages (open-rigs C# libraries)
├── scripts/
│   ├── deploy.cmd                  # Deploy script (Windows CMD)
│   └── deploy.ps1                  # Deploy script (PowerShell)
└── src/
    ├── open_rigs_my_experiment/     # Python module (your schema definitions)
    │   ├── __init__.py             # Package version helpers (__version__, __semver__)
    │   ├── regenerate.py           # Script to compile schemas and run sgen
    │   ├── rig.py                  # Rig schema definition
    │   └── task.py                 # Task schema definition
    ├── Extensions.csproj           # .NET project for generated C# extensions
    └── main.bonsai                 # Main Bonsai workflow
```

### Key directories

**.bonsai/**
The self-contained Bonsai environment for this project. Running `Setup.cmd` installs all required Bonsai packages into this directory so that the experiment is reproducible and isolated from other Bonsai installations on the machine.

**src/open_rigs_my_experiment/**
The Python module where you define your experiment schemas. This is the main file you will edit when specifying experiment parameters. The module name is derived automatically from your `prefix` and `project_name` inputs.

**src/Extensions.csproj**
A .NET project that Bonsai uses to compile the generated C# schema classes (produced by `regenerate.py`). You generally do not need to edit this file directly.

**examples/**
Jinja-templated example scripts that demonstrate how to instantiate each schema. These are useful as a starting point for creating parameter JSON files to pass to a running experiment.

**local_packages/**
Pre-built NuGet packages for the open-rigs C# libraries the workflow uses (`OpenRigs.Core`, `OpenRigs.Devices`, `OpenRigs.Logging`, `OpenRigs.Video`, `OpenRigs.Trials` for a task's trial loop, with an opto adapter for a task with optogenetics (`OpenRigs.Opto.ZapitBonsai`, `OpenRigs.Opto.ZapitMatlab`), and on a rig with speakers `OpenRigs.Audio`). These are referenced by the Bonsai environment via the local NuGet feed configured in `.bonsai/NuGet.config`.

**scripts/**
Helper scripts for deploying the project to a rig machine. See the [Build and Deploy](../tutorials/building-deploying.md) section for details.

### The schema compile step

Before opening Bonsai you need to run `regenerate.py` at least once. This script:

1. Combines the `rig.py` and `task.py` schemas into a single JSON schema file at `src/DataSchemas/<python_folder_name>.json`
2. Calls `Bonsai.Sgen` to generate `src/Extensions/<python_class_prefix>.Generated.cs` — the C# classes Bonsai will use to read your parameter files

```
uv run src/open_rigs_my_experiment/regenerate.py
```

You will need to re-run this step any time you change your Python schemas. The generated files are committed to the repository so that the project can be built on a rig machine without a Python environment.
