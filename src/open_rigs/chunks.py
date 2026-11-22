"""Chunked files: a log's or a device's files, each `<Name>_<time><suffix>`, the time when the experiment or the chunk
started, as OpenRigs.Core's FormatDate writes it: yyyy-MM-ddTHH-mm-ss±HH-MM, the rig's local time and its UTC offset.
Older names have no offset and are UTC (yyyy-MM-ddTHH-mm-ss); both are read.

Every reader of chunked files takes only these names, the name matched exactly (names hold _: WorkflowData_Trials,
a Harp device's <Device>_<Address>), and skips any other file: a converted experiment keeps other files beside its
chunks, such as a video's _crf35 copy. Readers order chunks by parse_time, never by name: around a clock change,
names do not sort as text. Standard library only, so a reader that cannot import open_rigs can load this file to test
its copy of TIME against it.
"""

import re
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import List, Tuple

TIME = r"\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}(?:[+-]\d{2}-\d{2})?"


def parse_time(token: str) -> datetime:
    """The instant a time token names, timezone-aware: at its offset, or UTC when it has none."""
    if not re.fullmatch(TIME, token):
        raise ValueError(f"{token!r} is not a time token (yyyy-MM-ddTHH-mm-ss, optionally ±HH-MM)")
    at = datetime.strptime(token[:19], "%Y-%m-%dT%H-%M-%S")
    if len(token) == 19:
        return at.replace(tzinfo=timezone.utc)
    offset = timedelta(hours=int(token[20:22]), minutes=int(token[23:25]))
    return at.replace(tzinfo=timezone(-offset if token[19] == "-" else offset))


def pattern(name: str, suffix: str) -> "re.Pattern[str]":
    """The whole file name `<name>_<time><suffix>`, for fullmatch."""
    return re.compile(rf"{re.escape(name)}_({TIME}){re.escape(suffix)}")


def files(folder: Path, name: str, suffix: str) -> Tuple[List[Path], List[Path]]:
    """The folder's chunks of `name` (`<name>_<time><suffix>`) in time order; and the other files named
    `<name>_...<suffix>`, which a reader skips. Both empty for a folder that is not there."""
    chunk = pattern(name, suffix)
    try:
        found = sorted(p for p in Path(folder).iterdir() if p.is_file())
    except OSError:
        return [], []
    near = [p for p in found if p.name.startswith(f"{name}_") and p.name.endswith(suffix)]
    matched = [(m, p) for p in near if (m := chunk.fullmatch(p.name))]
    ordered = [p for m, p in sorted(matched, key=lambda mp: parse_time(mp[0].group(1)))]
    return ordered, [p for p in near if not chunk.fullmatch(p.name)]


def time_of(path: Path) -> datetime:
    """The instant in a chunk's file name (its last `_<time>` before the suffix), for ordering chunks across suffixes."""
    found = re.findall(rf"_({TIME})", Path(path).name)
    if not found:
        raise ValueError(f"{Path(path).name} has no time token")
    return parse_time(found[-1])
