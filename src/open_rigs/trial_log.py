"""An experiment's BonsaiData_Trial as one typed table, by the workflow's own trial columns.

    from open_rigs import trial_log
    rows = trial_log.read(r"...\\sub-<mouse>\\ses-<id>_<DateTime>")

LogTrial writes the log hourly (`BonsaiData\\BonsaiData_Trial_<hour>.csv`, split at the Harp clock's
hour); this joins the files in name order, converts each column to its type (int, double, bool or string; an
empty cell is None, "NaN" a float NaN) and, with `decode`, replaces a coded column's number with its
name (`values`, such as a task's trialType). Columns the workflow's file does not list are kept as
written, as text. Nothing on disk is changed: recorded data is never rewritten.

What each column means comes from the experiment itself: `manifest.json`'s `trial_columns`, which
the workflow's ExperimentManifest copies from its `schemas/trial-columns.yml` at Start. For an
experiment whose manifest has none, give `repo`, a clone of the
workflow's repository: its file at the manifest's `source.commit`, or its current file when the
experiment has no manifest or the file did not exist at that commit.
"""

import csv
import json
import subprocess
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Tuple, Union

from ruamel.yaml import YAML

LOG = "BonsaiData_Trial"
MANIFEST = "manifest.json"
# Every version keeps `trial_columns` and `source.commit` as they were; any other is refused (records.md, rule 3).
MANIFEST_VERSIONS = (1, 2, 3)
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
    if manifest and manifest.get("version") not in MANIFEST_VERSIONS:
        raise ValueError(f"{manifest_path}: manifest version {manifest.get('version')!r} is not one this reads ({', '.join(map(str, MANIFEST_VERSIONS))})")
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


def _text_rows(experiment: Union[str, Path]) -> Iterator[Tuple[Path, int, Dict[str, str]]]:
    """Each row of the experiment's hourly files, BonsaiData/BonsaiData_Trial_<hour>.csv in name order, as written: its
    file, line number and cells by column name. A file's last line without a newline (still being written) is left out."""
    for path in sorted((Path(experiment) / LOG.split("_", 1)[0]).glob(f"{LOG}_*.csv")):
        text = path.read_text(encoding="utf-8-sig")
        if not text.endswith("\n"):
            text = text[: text.rfind("\n") + 1]
        lines = [r for r in csv.reader(text.splitlines()) if r]
        for number, cells in enumerate(lines[1:], 2):
            yield path, number, dict(zip(lines[0], cells))


def text_rows(experiment: Union[str, Path]) -> List[Dict[str, str]]:
    """Every row of the experiment's BonsaiData_Trial as written, in file order, each cell its text: what `read` types,
    for a reader that checks the cells itself (open_rigs.rigs.trials' summarise)."""
    return [row for _, _, row in _text_rows(experiment)]


def read(experiment: Union[str, Path], repo: Union[str, Path, None] = None, decode: bool = True) -> List[Dict[str, Any]]:
    """Every row of the experiment's BonsaiData_Trial, in file order, as a dict by column name.
    A file's last line without a newline (still being written) is left out. Raises ValueError naming
    the file, row and column of a cell that is not its column's type. An experiment with no trial log
    is no rows, without looking for its columns."""
    found = list(_text_rows(experiment))
    if not found:
        return []
    table = columns(experiment, repo)
    rows: List[Dict[str, Any]] = []
    for path, number, cells in found:
        row = {}
        for name, cell in cells.items():
            try:
                row[name] = _value(cell, table.get(name), decode)
            except ValueError as error:
                raise ValueError(f"{path.name} line {number}, {name}: {error}") from None
        rows.append(row)
    return rows
