"""An experiment's BonsaiData_Trial as one typed table, by the workflow's own trial columns.

    from open_rigs import trials
    rows = trials.read(r"...\\sub-<mouse>\\ses-<id>_<DateTime>")

LogTrial writes the log hourly (`BonsaiData\\BonsaiData_Trial_<hour>.csv`, split at the Harp clock's
hour); this joins the files in name order, renames each column recorded under an old name
(`formerly`) to its current one, converts each column to its type (int, double, bool or string; an
empty cell is None, "NaN" a float NaN) and, with `decode`, replaces a coded column's number with its
name (`values`, such as av-dome's trialType). Columns the workflow's file does not list are kept as
written, as text. Nothing on disk is changed: recorded data is never rewritten.

What each column means comes from the experiment itself: `manifest.json`'s `trial_columns`, which
the workflow's ExperimentManifest copies from its `schemas/trial-columns.yml` at Start (since
OpenRigs.Manifest 0.1.1-coen.4). For an experiment recorded before that, give `repo`, a clone of the
workflow's repository: its file at the manifest's `source.commit`, or its current file when the
experiment has no manifest or the file did not exist at that commit.
"""

import csv
import json
import subprocess
from pathlib import Path
from typing import Any, Dict, List, Optional, Union

from ruamel.yaml import YAML

LOG = "BonsaiData_Trial"
MANIFEST = "manifest.json"
REPO_FILE = "schemas/trial-columns.yml"


def _parse(text: str) -> Dict[str, Dict[str, Any]]:
    return json.loads(json.dumps(YAML(typ="safe").load(text) or {}))


def _from_repo(repo: Path, commit: Optional[str]) -> Optional[Dict[str, Dict[str, Any]]]:
    if commit:
        shown = subprocess.run(["git", "-C", str(repo), "show", f"{commit}:{REPO_FILE}"],
                               capture_output=True, text=True, encoding="utf-8")
        if shown.returncode == 0:
            return _parse(shown.stdout)
    current = repo / REPO_FILE
    return _parse(current.read_text(encoding="utf-8")) if current.is_file() else None


def columns(experiment: Union[str, Path], repo: Union[str, Path, None] = None) -> Dict[str, Dict[str, Any]]:
    """Every trial column the experiment's workflow lists, name: its entry. Raises ValueError when
    neither the experiment's manifest nor `repo` has them."""
    manifest_path = Path(experiment) / MANIFEST
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig")) if manifest_path.is_file() else {}
    section = manifest.get("trial_columns") or {}
    if "columns" in section:
        return section["columns"]
    if repo is not None:
        found = _from_repo(Path(repo), (manifest.get("source") or {}).get("commit"))
        if found is not None:
            return found
    why = section.get("why") or ("its manifest has no trial_columns" if manifest else f"it has no {MANIFEST}")
    raise ValueError(f"{experiment}: no trial columns ({why}); give repo, a clone of the workflow's repository with {REPO_FILE}")


def _value(text: str, column: Optional[Dict[str, Any]], decode: bool) -> Any:
    if column is None:
        return text
    if text == "":
        return None
    kind = column["type"]
    if kind == "int":
        value: Any = int(float(text)) if text not in ("NaN", "nan") else None
        codes = column.get("values")
        if decode and isinstance(codes, dict) and value is not None:
            return codes.get(str(value), value)
        return value
    if kind == "double":
        return float(text)
    if kind == "bool":
        lowered = text.strip().lower()
        if lowered not in ("true", "false"):
            raise ValueError(f"{text!r} is not True or False")
        return lowered == "true"
    return text


def read(experiment: Union[str, Path], repo: Union[str, Path, None] = None, decode: bool = True) -> List[Dict[str, Any]]:
    """Every row of the experiment's BonsaiData_Trial, in file order, as a dict by current column name.
    A file's last line without a newline (still being written) is left out. Raises ValueError naming
    the file, row and column of a cell that is not its column's type. An experiment with no trial log
    is no rows, without looking for its columns."""
    folder = Path(experiment) / LOG.split("_", 1)[0]
    paths = sorted(folder.glob(f"{LOG}_*.csv"))
    if not paths:
        return []
    table = columns(experiment, repo)
    renamed = {old: name for name, entry in table.items() for old in entry.get("formerly") or []}
    rows: List[Dict[str, Any]] = []
    for path in paths:
        text = path.read_text(encoding="utf-8-sig")
        if not text.endswith("\n"):
            text = text[: text.rfind("\n") + 1]
        lines = [r for r in csv.reader(text.splitlines()) if r]
        if not lines:
            continue
        header = [renamed.get(h, h) for h in lines[0]]
        for number, cells in enumerate(lines[1:], 2):
            row = {}
            for name, cell in zip(header, cells):
                try:
                    row[name] = _value(cell, table.get(name), decode)
                except ValueError as error:
                    raise ValueError(f"{path.name} line {number}, {name}: {error}") from None
            rows.append(row)
    return rows
