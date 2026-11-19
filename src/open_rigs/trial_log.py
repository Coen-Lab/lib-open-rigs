"""An experiment's WorkflowData_Trials as one typed table, by the workflow's own trial columns.

    from open_rigs import trial_log
    rows = trial_log.read(r"...\\sub-<mouse>\\ses-<id>_<DateTime>")

LogTrials writes one file for the experiment (`WorkflowData\\WorkflowData_Trials_<start>.csv`, start the
experiment folder's); this reads it, converts each column to its type (int, double, bool or string; an
empty cell is None, "NaN" a float NaN) and, with `decode`, replaces a coded column's number with its
name (`values`, such as a task's trialType). Columns the workflow's file does not list are kept as
written, as text. Nothing on disk is changed: recorded data is never rewritten.

A trial's task values come from the experiment's combinations table, Settings/Settings_Combinations_<time>.json
({"rows": [{"trialCombinationId", "trialConditionId", "trialType", "conditionWeight", "values": {dotted path: value}}]},
written at Go), joined on the log's trialCombinationId; `check` holds the recorded columns to it.

What each column means comes from the experiment itself: `manifest.json`'s `trial_columns`, which
the workflow's ExperimentManifest copies from its `schemas/trial-columns.yml` at Start. For an
experiment whose manifest has none, give `repo`, a clone of the
workflow's repository: its file at the manifest's `source.commit`, or its current file when the
experiment has no manifest or the file did not exist at that commit.
"""

import csv
import json
import math
import subprocess
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Tuple, Union

from ruamel.yaml import YAML

from open_rigs import chunks
from open_rigs.core.task import MUTE
from open_rigs.rigs import conditions

LOG = "WorkflowData_Trials"
MANIFEST = "manifest.json"
# Every version keeps `trial_columns` and `source.commit` as they were; any other is refused (records.md, rule 3).
MANIFEST_VERSIONS = (1, 2, 3, 4)  # 4 is what OpenRigs.Manifest writes (ManifestWriter.Version)
REPO_FILE = "schemas/trial-columns.yml"
TASK_FILE = ("Settings", "Settings_Task", ".json")  # written beside the trial log at Go
COMBINATIONS_FILE = ("Settings", "Settings_Combinations", ".json")  # every combination built at Go, one row each
ID = "trialCombinationId"  # a trial's row in it
HELD = ("trialConditionId", "trialType")  # columns every row has, held to its combination's
PREFIX = "task."  # a combination's value as a column, by its dotted path


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
    """Each row of the experiment's WorkflowData/WorkflowData_Trials_<start>.csv (several in name order), as written: its
    file, line number and cells by column name. A file's last line without a newline (still being written) is left out.
    Only chunk names are read (open_rigs.chunks); any other file is skipped."""
    for path in chunks.files(Path(experiment) / LOG.split("_", 1)[0], LOG, ".csv")[0]:
        text = path.read_text(encoding="utf-8-sig")
        if not text.endswith("\n"):
            text = text[: text.rfind("\n") + 1]
        lines = [r for r in csv.reader(text.splitlines()) if r]
        for number, cells in enumerate(lines[1:], 2):
            yield path, number, dict(zip(lines[0], cells))


def text_rows(experiment: Union[str, Path]) -> List[Dict[str, str]]:
    """Every row of the experiment's WorkflowData_Trials as written, in file order, each cell its text: what `read` types,
    for a reader that checks the cells itself (open_rigs.rigs.trials' summarise)."""
    return [row for _, _, row in _text_rows(experiment)]


def _one(experiment: Path, name: Tuple[str, str, str]) -> Path:
    found = chunks.files(experiment / name[0], name[1], name[2])[0]
    if len(found) != 1:
        raise ValueError(f"{experiment}: {len(found)} files {name[0]}/{name[1]}_<time>{name[2]}, need exactly one")
    return found[0]


def _normal(value: Any) -> Any:
    """A task value as the combinations table holds it: a one-entry list is its entry."""
    return value[0] if isinstance(value, list) and len(value) == 1 else value


def _number(value: Any) -> Optional[float]:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    try:
        return float(value) if isinstance(value, str) else None  # a manifest's scalars arrive as text
    except ValueError:
        return None


def _same(recorded: Any, expected: Any) -> bool:
    """Equal, numbers by value (to 1e-6 relative), bare NaN and the text "NaN" both NaN."""
    a, b = _number(recorded), _number(expected)
    if a is not None and b is not None:
        return (math.isnan(a) and math.isnan(b)) or math.isclose(a, b, rel_tol=1e-6, abs_tol=1e-12)
    return recorded == expected


def _is_nan(value: Any) -> bool:
    number = _number(value)
    return number is not None and math.isnan(number)


def _combination_rows(experiment: Path, rows: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Each trial's row of the experiment's combinations table, by its trialCombinationId."""
    table = json.loads(_one(experiment, COMBINATIONS_FILE).read_text(encoding="utf-8-sig"))["rows"]
    by_id = {entry[ID]: entry for entry in table}
    found = []
    for number, row in enumerate(rows, 1):
        if row.get(ID) not in by_id:
            raise ValueError(f"{experiment}: trial {number}'s {ID} {row.get(ID)!r} is not a row of {COMBINATIONS_FILE[1]} ({len(by_id)} rows)")
        found.append(by_id[row[ID]])
    return found


def _problems(experiment: Path, rows: List[Dict[str, Any]], combos: List[Dict[str, Any]]) -> List[str]:
    """Where the recorded columns disagree with their trials' combinations (`check`): a held column or key missing is one too."""
    problems = [f"{name}: not in the log" for name in HELD if rows and name not in rows[0]]
    problems += [f"{COMBINATIONS_FILE[1]} row {combo[ID]} has no {name}" for combo in {c[ID]: c for c in combos}.values() for name in HELD if name not in combo]
    if problems:
        return problems
    for number, (row, combo) in enumerate(zip(rows, combos), 1):
        for name in HELD:
            if row.get(name) != combo[name]:
                problems.append(f"trial {number}: {name} {row.get(name)!r}, its combination {combo[ID]}'s is {combo[name]!r}")
    manifest_path = experiment / MANIFEST
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig")) if manifest_path.is_file() else {}
    listed = (manifest.get("trial_columns") or {}).get("columns") or {}
    copies = {name: entry for name, entry in listed.items() if entry.get("copyOf")}
    if not copies:
        return problems
    task = {path: _normal(value) for path, value in conditions.flatten(
        json.loads(_one(experiment, TASK_FILE).read_text(encoding="utf-8-sig"))["taskParameters"]).items()}
    for name, entry in copies.items():
        path, when = entry["copyOf"], entry.get("nanWhen")
        if path not in task and not all(path in combo["values"] for combo in combos):
            problems.append(f"{name}: copyOf {path}, a path in neither {TASK_FILE[1]} nor every combination")
            continue
        for number, (row, combo) in enumerate(zip(rows, combos), 1):
            if name not in row:
                problems.append(f"{name}: not in the log, though it copies {path}")
                break
            if when and _same(row.get(when["column"]), when["equals"]):
                if not _is_nan(row[name]):
                    problems.append(f"trial {number}: {name} {row[name]!r}, not NaN where {when['column']} is {when['equals']}")
                continue
            expected = _normal(combo["values"][path]) if path in combo["values"] else task[path]
            if expected == MUTE:  # a level that plays nothing is logged as NaN
                expected = math.nan
            if not _same(row[name], expected):
                problems.append(f"trial {number}: {name} {row[name]!r}, its combination {combo[ID]}'s {path} is {expected!r}")
    return problems


def _raise(experiment: Path, problems: List[str]) -> None:
    if problems:
        shown = "\n  ".join(problems[:20]) + (f"\n  and {len(problems) - 20} more" if len(problems) > 20 else "")
        raise ValueError(f"{experiment}: {len(problems)} recorded value(s) disagree with {COMBINATIONS_FILE[1]}:\n  {shown}")


def _typed(experiment: Path, repo: Union[str, Path, None], decode: bool) -> List[Dict[str, Any]]:
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


def check(experiment: Union[str, Path]) -> None:
    """Raises ValueError listing every trial whose recorded columns disagree with its row of the experiment's combinations
    table (Settings/Settings_Combinations_<time>.json): its trialConditionId and trialType, and each column whose entry in
    the manifest's own trial_columns has copyOf, a task path: the row's value, else Settings_Task's (a one-entry list its
    entry; a level's mute is NaN), or NaN on the trials nanWhen {column, equals} names. A column or table key it holds that is missing is a problem,
    never skipped. A log without trialCombinationId has nothing to check."""
    experiment = Path(experiment)
    rows = _typed(experiment, None, True)
    if any(ID in row for row in rows):
        _raise(experiment, _problems(experiment, rows, _combination_rows(experiment, rows)))


def read(experiment: Union[str, Path], repo: Union[str, Path, None] = None, decode: bool = True) -> List[Dict[str, Any]]:
    """Every row of the experiment's WorkflowData_Trials, in file order, as a dict by column name.
    A file's last line without a newline (still being written) is left out. Raises ValueError naming
    the file, row and column of a cell that is not its column's type. An experiment with no trial log
    is no rows, without looking for its columns.

    A log with trialCombinationId also gets its combination's values from the combinations table
    (Settings/Settings_Combinations_<time>.json), each as a column `task.<dotted path>`, once `check` passes. Raises
    ValueError for no or several tables, an id not in it, a recorded value that disagrees (check), a recorded column of a
    `task.` name. A log without trialCombinationId is read as written."""
    experiment = Path(experiment)
    rows = _typed(experiment, repo, decode)
    if not any(ID in row for row in rows):
        return rows
    combos = _combination_rows(experiment, rows)
    _raise(experiment, _problems(experiment, rows, combos))
    paths = sorted({path for combo in combos for path in combo["values"]})
    for number, (row, combo) in enumerate(zip(rows, combos), 1):
        for path in paths:
            name = PREFIX + path
            if name in row:
                raise ValueError(f"{experiment}: trial {number} has a recorded column {name}, which its combination's {path} would overwrite")
            if path not in combo["values"]:
                raise ValueError(f"{experiment}: {COMBINATIONS_FILE[1]} row {combo[ID]} has no {path}, which other rows have")
            row[name] = combo["values"][path]
    return rows
