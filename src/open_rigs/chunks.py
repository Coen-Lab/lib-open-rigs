"""Chunked files: a log's or a device's files, each `<Name>_<time><suffix>`, the time when the experiment or the hour
started, as OpenRigs.Core's FormatDate writes it (yyyy-MM-ddTHH-mm-ss).

Every reader of chunked files takes only these names, the name matched exactly (names hold _: WorkflowData_Trials,
a Harp device's <Device>_<Address>), and skips any other file: a converted experiment keeps other files beside its
chunks, such as a video's _crf35 copy. Standard library only, so a reader that cannot import open_rigs can load this
file to test its copy of TIME against it.
"""

import re
from pathlib import Path
from typing import List, Tuple

TIME = r"\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}"


def pattern(name: str, suffix: str) -> "re.Pattern[str]":
    """The whole file name `<name>_<time><suffix>`, for fullmatch."""
    return re.compile(rf"{re.escape(name)}_{TIME}{re.escape(suffix)}")


def files(folder: Path, name: str, suffix: str) -> Tuple[List[Path], List[Path]]:
    """The folder's chunks of `name` (`<name>_<time><suffix>`) in name order, so time order; and the other files
    named `<name>_...<suffix>`, which a reader skips. Both empty for a folder that is not there."""
    chunk = pattern(name, suffix)
    try:
        found = sorted(p for p in Path(folder).iterdir() if p.is_file())
    except OSError:
        return [], []
    near = [p for p in found if p.name.startswith(f"{name}_") and p.name.endswith(suffix)]
    return [p for p in near if chunk.fullmatch(p.name)], [p for p in near if not chunk.fullmatch(p.name)]
