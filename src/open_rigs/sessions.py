"""Find and load one workflow's sessions for analysis, by its repository's name.

A session is `<root>/sub-<mouse>/ses-<id>_<DateTime>`, as the lab's ingest puts them on the server (Live-Data, which
holds every workflow's); its trials come from WorkflowData_Trials typed by the session's own manifest (trial_log), and
its task from Settings_Task. Nothing here runs on import."""

import json
from pathlib import Path
from typing import Any

from open_rigs import trial_log


def _manifest(session: Path) -> dict[str, Any]:
    try:
        return json.loads((Path(session) / "manifest.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def _settings(session: Path, kind: str) -> dict[str, Any]:
    """The session's Settings_<kind>_*.json (the first), or {} when it has none."""
    files = sorted((Path(session) / "Settings").glob(f"Settings_{kind}_*.json"))
    return json.loads(files[0].read_text(encoding="utf-8-sig")) if files else {}


def is_workflow(session: Path, repo: str) -> bool:
    """True for a session of the repository named `repo` (any owner, fork or branch): its manifest's source.repo."""
    source = ((_manifest(session).get("source") or {}).get("repo") or "").rstrip("/").rsplit("/", 1)[-1].lower()
    return source.removesuffix(".git") == repo.lower()


def find_sessions(root: Path, repo: str, subjects: list[str] | None = None) -> list[Path]:
    """`repo`'s session folders under root, by `sub-` folder (every mouse when subjects is None)."""
    found = []
    for mouse in sorted(Path(root).glob("sub-*")):
        if subjects is None or mouse.name[4:] in subjects:
            found += [s for s in sorted(mouse.glob("ses-*")) if s.is_dir() and is_workflow(s, repo)]
    return found


def load_sessions(root: Path, repo: str, subjects: list[str] | None = None):
    """Every trial of `repo`'s sessions as one pandas DataFrame: the typed columns plus subject, session and session_path."""
    import pandas as pd

    frames = []
    for session in find_sessions(root, repo, subjects):
        rows = trial_log.read(session)
        if rows:
            frame = pd.DataFrame(rows)
            frame["subject"] = session.parent.name[4:]
            frame["session"] = session.name
            frame["session_path"] = str(session)
            frames.append(frame)
    return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()


def _task(session: Path) -> dict[str, Any] | None:
    task = _settings(session, "Task")
    return task["taskParameters"] if task else None


def curriculum_stage(session: Path) -> dict[str, Any] | None:
    """The curriculum stage a session ran ({id, code, version, path, label, sha256}), its Settings_Task's `curriculum`:
    None when the task file has none or there is no task file."""
    task = _task(session)
    return task.get("curriculum") if task else None


def trial_conditions(session: Path) -> list[dict[str, Any]] | None:
    """The trialConditions a session ran with, from its Settings_Task: the list the trialConditionId column indexes, each
    {trialType, weight, pairs, set} (a missing set as {}). None without a task file."""
    task = _task(session)
    return None if task is None else [{**c, "set": c.get("set") or {}} for c in task["trialConditions"]]


def trial_weights(session: Path) -> dict[str, float] | None:
    """The weight of each trial type a session ran with, its conditions' weights added by trialType. None without a task file."""
    conditions = trial_conditions(session)
    if conditions is None:
        return None
    weights: dict[str, float] = {}
    for c in conditions:
        weights[c["trialType"]] = weights.get(c["trialType"], 0) + c["weight"]
    return weights
