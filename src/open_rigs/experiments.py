"""Find and load one workflow's experiments for analysis, by its repository's name.

An experiment is one workflow run, `<root>/sub-<mouse>/ses-<id>_<DateTime>`, as the lab's ingest puts them on the server
(Live-Data, which holds every workflow's); its trials come from WorkflowData_Trials typed by the experiment's own manifest
(trial_log), and its task from Settings_Task. Nothing here runs on import."""

import json
from pathlib import Path
from typing import Any

from open_rigs import trial_log


def _manifest(experiment: Path) -> dict[str, Any]:
    try:
        return json.loads((Path(experiment) / "manifest.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def _settings(experiment: Path, kind: str) -> dict[str, Any]:
    """The experiment's Settings_<kind>_*.json (the first), or {} when it has none."""
    files = sorted((Path(experiment) / "Settings").glob(f"Settings_{kind}_*.json"))
    return json.loads(files[0].read_text(encoding="utf-8-sig")) if files else {}


def is_workflow(experiment: Path, repo: str) -> bool:
    """True for an experiment of the repository named `repo` (any owner, fork or branch): its manifest's source.repo."""
    source = ((_manifest(experiment).get("source") or {}).get("repo") or "").rstrip("/").rsplit("/", 1)[-1].lower()
    return source.removesuffix(".git") == repo.lower()


def find_experiments(root: Path, repo: str, subjects: list[str] | None = None) -> list[Path]:
    """`repo`'s experiment folders under root, by `sub-` folder (every mouse when subjects is None)."""
    found = []
    for mouse in sorted(Path(root).glob("sub-*")):
        if subjects is None or mouse.name[4:] in subjects:
            found += [s for s in sorted(mouse.glob("ses-*")) if s.is_dir() and is_workflow(s, repo)]
    return found


def load_experiments(root: Path, repo: str, subjects: list[str] | None = None):
    """Every trial of `repo`'s experiments as one pandas DataFrame: the typed columns plus subject, experiment and experiment_path."""
    import pandas as pd

    frames = []
    for experiment in find_experiments(root, repo, subjects):
        rows = trial_log.read(experiment)
        if rows:
            frame = pd.DataFrame(rows)
            frame["subject"] = experiment.parent.name[4:]
            frame["experiment"] = experiment.name
            frame["experiment_path"] = str(experiment)
            frames.append(frame)
    return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()


def _task(experiment: Path) -> dict[str, Any] | None:
    task = _settings(experiment, "Task")
    return task["taskParameters"] if task else None


def curriculum_stage(experiment: Path) -> dict[str, Any] | None:
    """The curriculum stage an experiment ran ({id, code, version, path, label, sha256}), its Settings_Task's `curriculum`:
    None when the task file has none or there is no task file."""
    task = _task(experiment)
    return task.get("curriculum") if task else None


def trial_conditions(experiment: Path) -> list[dict[str, Any]] | None:
    """The trialConditions an experiment ran with, from its Settings_Task: the list the trialConditionId column indexes, each
    {trialType, weight, pairs, set} (a missing set as {}), its trialType the one derived at Go (Settings_Combinations'
    rows). None without a task file."""
    task = _task(experiment)
    if task is None:
        return None
    names = {row["trialConditionId"]: row["trialType"] for row in _settings(experiment, "Combinations").get("rows", [])}
    return [{"trialType": names[i], **c, "set": c.get("set") or {}} for i, c in enumerate(task["trialConditions"])]


def trial_weights(experiment: Path) -> dict[str, float] | None:
    """The weight of each trial type an experiment ran with, its conditions' weights added by trialType. None without a task file."""
    conditions = trial_conditions(experiment)
    if conditions is None:
        return None
    weights: dict[str, float] = {}
    for c in conditions:
        weights[c["trialType"]] = weights.get(c["trialType"], 0) + c["weight"]
    return weights
