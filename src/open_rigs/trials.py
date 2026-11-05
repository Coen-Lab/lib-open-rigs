"""An experiment's BonsaiData_Trial as one typed table, by lab_names.yml.

    from open_rigs import trials
    rows = trials.read(r"...\\sub-<mouse>\\ses-<id>_<DateTime>")

LogData writes the log hourly (`BonsaiData\\BonsaiData_Trial_<hour>.csv`, split at the Harp clock's
hour); this joins the files in name order, renames each column recorded under an old name
(`formerly`) to its current one, converts each column to its type (int, float, bool or str; an
empty cell is None, "NaN" a float NaN) and, with `decode`, replaces a coded column's number with its
name (`values`, such as av-dome's trialType). Columns lab_names.yml does not list are kept as
written, as text. Nothing on disk is changed: recorded data is never rewritten.
"""

import csv
from pathlib import Path
from typing import Any, Dict, List, Optional, Union

from open_rigs import lab_names

LOG = "BonsaiData_Trial"


def columns(rig: Optional[str] = None, names: Optional[Dict[str, Any]] = None) -> Dict[str, Dict[str, Any]]:
    """Every trial column lab_names.yml lists, name: its entry; the lab's and `rig`'s, or every rig's."""
    names = names if names is not None else lab_names.load()
    out = dict(names["trialColumns"])
    for name, entry in names["rigs"].items():
        if rig is None or name == rig:
            out.update(entry.get("trialColumns") or {})
    return out


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


def read(experiment: Union[str, Path], rig: Optional[str] = None, decode: bool = True) -> List[Dict[str, Any]]:
    """Every row of the experiment's BonsaiData_Trial, in file order, as a dict by current column name.
    A file's last line without a newline (still being written) is left out. Raises ValueError naming
    the file, row and column of a cell that is not its column's type."""
    table = columns(rig)
    renamed = {old: name for name, entry in table.items() for old in entry.get("formerly") or []}
    rows: List[Dict[str, Any]] = []
    folder = Path(experiment) / LOG.split("_", 1)[0]
    for path in sorted(folder.glob(f"{LOG}_*.csv")):
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
