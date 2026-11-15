# Building and Deploying

> **Lab workflows differ.** This page teaches the generic open-rigs template, where a project has its own rig model in `rig.py` and its own `examples\rig.py`. A lab workflow has neither: every lab rig is `open_rigs.rigs.lab_rig.LabRig`, read from the machine's `rig.yml` (camelCase keys such as `behaviorBoard`, `timestampGeneratorGen3`, `licketySplit` and `camera`, each device kind a group of named entries; `open_rigs\rigs\rig-template.yml` is the whole file), and its devices are the pieces in [Building Blocks](../articles/building-blocks.md). A new lab device is a new part of `LabRig`, not a field of a project's rig.

### Template Project
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

Using a browser create a new, empty repository on GitHub (e.g. under your personal account, or your institution's organisation account) with the same name as your local project. Set this GitHub repository as the `remote` for your project:
```
git remote add origin https://github.com/<github-account>/my-new-project.git
```

Push your local changes to the `remote` repository:
```
git push -u origin main
```

### Deploy Project
The open-rigs template comes with a script to 'bootstrap' the Python and Bonsai development environments, run this with:
```
.\scripts\deploy.ps1
```