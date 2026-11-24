# Create a new repository from the `open-rigs/open-rigs` template using Copier

This guide explains how to initialize and create a new open-ucl repository from the template
in `open-rigs/open-rigs` using the **Copier** CLI. This tool allows you to recreate the base components of an open-ucl project, including the base Bonsai and Python environments required to get started.

All powershell commands can be run from the command line or from a terminal in VSCode.

Template source:  
https://github.com/open-rigs/open-rigs (subdirectory: `template/`)

Copier documentation:  
https://copier.readthedocs.io/en/stable/

---

## Prerequisites

Ensure the following are installed:

- **Windows 10 or newer**
- **Python >=3.13 + <3.14**
  - Download from https://www.python.org/downloads/windows/
  - Make sure **“Add Python to PATH”** is checked during installation
- **Git 2.27+**
  - Download from https://git-scm.com/download/win

---

## Install `uv`

`uv` is a very fast package and tool manager for Python. We recommend this for all your Python needs

Install it using a command line terminal:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
```

Restart your terminal before continuing, so that we can pick up the new paths

---

## Install Copier using `uv`

In a command line terminal, install Copier as a global CLI tool:

```powershell
uv tool install copier
```
---

## Create a new project from the template

### 1. Choose the template branch

Decide which branch (or tag) of the template you want to use, for example:

- `main`
- `dev`
- `v1.0.0`

---

### 2. Generate the project

Run Copier from the directory where you want the new project folder created:

Example:

```powershell
copier copy --vcs-ref HEAD gh:open-rigs/open-rigs --subdirectory template ./my-new-project
```
The `--vcs-ref` flag specifies a branch or tag. Use a specific release tag (e.g. `v0.1.0`) to pin to a known-good version, or `HEAD` to use the latest commit on the default branch.
Copier will prompt you for some basic template variables and then generate the files and directory structure.

---

## Initialize your new Git repository

### 3. Create an empty repository for your new project
Using a browser, create a new, empty repository on GitHub (do not add a README or license, the template will sort that).

### 4. Initialize Git with your chosen default branch
1. Open an instance of VSCode and open your new project folder

2. Delete any hangover `.git` directory metadata (if it exists).

3. Initialize a local git repository:

```powershell
git init -b main
```
4. Add the remote:

```powershell
git remote add origin https://github.com/open-rigs/my-new-project.git
```
5. Commit
```
git add .
git commit -m "Deploy new open-rigs rig"
```
6. Push
```
git push -u origin main
```

### 5. Run the `deploy` script

``` 
./scripts/deploy.cmd
```
---
### Congrats! 
You have created a new project with all open-ucl dependencies and base components to begin your new rig or experiment project!

---

