"""An experiment's WorkflowData_Trials as one typed table, by the workflow's own trial columns.

    from open_rigs import trial_log
    rows = trial_log.read(r"...\\sub-<mouse>\\ses-<id>_<DateTime>")

LogTrials writes one file for the experiment (`WorkflowData\\WorkflowData_Trials_<start>.csv`, start the
experiment folder's); this reads it, converts each column to its type (int, double, bool or string; an
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
from typing import Any, Dict, Iterable, Iterator, List, Optional, Tuple, Union

from ruamel.yaml import YAML

from open_rigs import chunks
from open_rigs.rigs import conditions

LOG = "WorkflowData_Trials"
MANIFEST = "manifest.json"
# Every version keeps `trial_columns` and `source.commit` as they were; any other is refused (records.md, rule 3).
MANIFEST_VERSIONS = (1, 2, 3, 4)  # 4 is what OpenRigs.Manifest writes (ManifestWriter.Version)
REPO_FILE = "schemas/trial-columns.yml"
TASK_FILE = ("Settings", "Settings_Task", ".json")  # written beside the trial log at Go
SCHEMA_DIR = "src/DataSchemas"  # a task repo's generated schemas, its task schema *_task.json


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


def _git(repo: Path, *args: str) -> Optional[str]:
    shown = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True, encoding="utf-8")
    return shown.stdout if shown.returncode == 0 else None


def list_fields_of(experiment: Union[str, Path], repo: Union[str, Path]) -> List[str]:
    """The task's list fields (conditions.pick_lists) from the repo's task schema, SCHEMA_DIR/*_task.json, at the run's
    commit (its manifest's source.commit), else as the clone has it now. Raises ValueError when the repo has none."""
    repo, manifest_path = Path(repo), Path(experiment) / MANIFEST
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig")) if manifest_path.is_file() else {}
    commit = (manifest.get("source") or {}).get("commit")
    names = _git(repo, "ls-tree", "--name-only", f"{commit}:{SCHEMA_DIR}") if commit else None
    name = next((n for n in (names or "").split() if n.endswith("_task.json")), None)
    text = _git(repo, "show", f"{commit}:{SCHEMA_DIR}/{name}") if name else None
    if text is None:
        found = sorted((repo / SCHEMA_DIR).glob("*_task.json"))
        if len(found) != 1:
            raise ValueError(f"{repo}: {len(found)} task schemas in {SCHEMA_DIR}, need exactly one for the task's list fields")
        text = found[0].read_text(encoding="utf-8")
    schema = json.loads(text)
    defs = schema.get("$defs", {})
    params = next((d for d in defs.values() if isinstance(d, dict) and "x-max-combinations" in d), None)
    if params is None:
        raise ValueError(f"{repo}: its task schema has no x-max-combinations, so no task parameters to find list fields in")
    return conditions.pick_lists({**params, "$defs": defs})


def _task(experiment: Path) -> Dict[str, Any]:
    found = chunks.files(experiment / TASK_FILE[0], TASK_FILE[1], TASK_FILE[2])[0]
    if len(found) != 1:
        raise ValueError(f"{experiment}: {len(found)} task files in {TASK_FILE[0]} (Settings_Task_<time>.json), need exactly one to decode trialCombination")
    return json.loads(found[0].read_text(encoding="utf-8-sig"))["taskParameters"]


def _combination_columns(experiment: Path, rows: List[Dict[str, Any]], list_fields: Iterable[str]) -> None:
    """Adds to each row the resolved value of every field that varies across the run's drawn combinations, by dotted path.
    trialCondition indexes the task file's trialConditions and trialCombination that condition's combinations."""
    task = _task(experiment)
    base, flat = task, conditions.flatten(task)
    built: Dict[int, List[Dict[str, Any]]] = {}

    def resolved(index: Any, combination: Any, line: int) -> Dict[str, Any]:
        if not isinstance(index, int) or not 0 <= index < len(task["trialConditions"]):
            raise ValueError(f"trial {line}: trialCondition {index!r} is not one of the task file's {len(task['trialConditions'])} trialConditions")
        if index not in built:
            c = task["trialConditions"][index]
            built[index] = conditions.condition_combinations(base, c.get("set") or {}, c.get("pairs") or [], list_fields)
        if not isinstance(combination, int) or not 0 <= combination < len(built[index]):
            raise ValueError(f"trial {line}: trialCombination {combination!r} is out of range, condition {index} has {len(built[index])} combinations")
        return {**flat, **built[index][combination]}

    values = [resolved(row.get("trialCondition"), row.get("trialCombination"), n) for n, row in enumerate(rows, 1)]
    varied = [path for path in flat if len({json.dumps(v[path]) for v in values}) > 1]
    for row, value in zip(rows, values):
        row.update({path: value[path] for path in varied})


def read(experiment: Union[str, Path], repo: Union[str, Path, None] = None, decode: bool = True,
         list_fields: Optional[Iterable[str]] = None) -> List[Dict[str, Any]]:
    """Every row of the experiment's WorkflowData_Trials, in file order, as a dict by column name.
    A file's last line without a newline (still being written) is left out. Raises ValueError naming
    the file, row and column of a cell that is not its column's type. An experiment with no trial log
    is no rows, without looking for its columns.

    A log with a trialCombination column also gets one column per task field that varied across its rows, named by the
    field's dotted path, decoded from the run's task file (Settings/Settings_Task_<time>.json) with trialCondition. The task's
    list fields, which the task file does not say, come from `repo`'s task schema (list_fields_of) unless `list_fields`
    gives them. Raises ValueError for a missing task file, neither repo nor list_fields, or an index out of range."""
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
    if any("trialCombination" in row for row in rows):
        if list_fields is None:
            if repo is None:
                raise ValueError(f"{experiment}: the log has trialCombination; give repo, a clone of the task's repository, to decode it")
            list_fields = list_fields_of(experiment, repo)
        _combination_columns(Path(experiment), rows, list_fields)
    return rows
