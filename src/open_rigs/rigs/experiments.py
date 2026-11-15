"""Where a mouse's experiments are, and the search every task's suggest-stage makes through them (standard library only).

An experiment is on the rig until rig-sync copies it, then in the server's staging, then filed in Live-Data;
production av-dome's are on its rig under C:\\data\\<dataset>\\ and filed in Live-Data under AV_Dome...\\sub-<id>\\.
A share that does not answer, an experiment that cannot be read, or one with only its manifest left (its data
ingested elsewhere) is said in `unread`, never passed over: it may be the mouse's last experiment.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable, Iterable, Optional, Sequence, Tuple

LIVE_DATA = Path(r"\\rdp.arc.ucl.ac.uk\ritd-ag-project-rd01n9-pcoen00")
ROOTS = [Path(r"C:\Coen-Lab-Local\Acquired-Data"), Path(r"\\128.40.51.157\rigs\staging"), LIVE_DATA, Path(r"C:\data")]


# Reads one experiment folder: (the experiment, which has `when`, or None when it does not count; whether only
# its manifest is left).
Reader = Callable[[Path], Tuple[Optional[Any], bool]]


def unanswered(root: Path) -> bool:
    """A share (\\\\server\\...) that cannot be listed: glob reads it as empty, which would look like a mouse with
    no history. A local folder that is not there just has none."""
    if not str(root).startswith("\\\\"):
        return False
    try:
        return not root.is_dir()
    except OSError:
        return True


def written_by(manifest: Path, repos: Iterable[str]) -> bool:
    """Whether an experiment's manifest.json names one of `repos` as its source.repo; one that cannot be read,
    or names none, counts, so no history is passed over unseen."""
    try:
        repo = json.loads(manifest.read_text(encoding="utf-8-sig")).get("source", {}).get("repo") or ""
    except (OSError, ValueError, AttributeError):
        return True
    return not repo or any(name in repo for name in repos)


def subject_folders(base: Path, subject: str, unread: list[str], eartag: str = "") -> list[Path]:
    """The mouse's experiment folders under one root (sub-<id>\\ses-* there or one folder down); none, and the
    root added to `unread`, if it does not answer. Runs before 13 Nov 2026 were filed under the eartag."""
    try:
        if unanswered(base):
            raise OSError(base)
        names = [n for n in dict.fromkeys((subject, eartag)) if n]
        return [f for n in names for f in (*base.glob(f"sub-{n}/ses-*"), *base.glob(f"*/sub-{n}/ses-*"))]
    except OSError:
        unread.append(f"{base} (No Answer)")
        return []


def find(subject: str, sources: Sequence[Tuple[Sequence[Path], Reader]], unread: list[str], eartag: str = "") -> list:
    """Every experiment of the mouse that its reader counts, newest first (by `when`): each source is (roots,
    reader). A folder seen twice (on the rig and in staging) counts once. A root that does not answer, an
    experiment its reader cannot read and one with only its manifest left in every root are added to `unread`."""
    found: dict = {}
    manifest_only: list[str] = []
    for roots, read in sources:
        for base in roots:
            for folder in subject_folders(base, subject, unread, eartag):
                if folder.name in found or not folder.is_dir():
                    continue
                try:
                    experiment, only_manifest = read(folder)
                except (OSError, ValueError, KeyError, TypeError) as error:
                    unread.append(f"{folder.name} (Unreadable: {type(error).__name__})")
                    continue
                if only_manifest:
                    manifest_only.append(folder.name)
                elif experiment is not None:
                    found[folder.name] = experiment
    unread.extend(f"{name} (Only Its Manifest Is Left)" for name in dict.fromkeys(manifest_only) if name not in found)
    return sorted(found.values(), key=lambda e: e.when, reverse=True)
