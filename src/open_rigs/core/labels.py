"""A task's condition labels as data: the schema's x-labels, evaluated here once for every task that has them.

x-labels is an ordered list; a condition takes the first entry whose every test holds, and no entry matching is an error.
Each test reads a path's value as the condition has it (its set, else the task's), a list of picks:
    {"label": L}                          always (a last catch-all)
    "when": path, "is": value             every pick is the value (x-rules' when/is)
    "anyAbove": {path: v, ...}            some pick is above v
    "anyNotIn": {path: [values], ...}     some pick is none of the values
    "mirrored": [a, b]                    a and b share a pairs group and b's list is a's negated
"""

from typing import Any

TESTS = {"label", "when", "is", "anyAbove", "anyNotIn", "mirrored"}


def _get(data: dict, dotted: str) -> Any:
    for part in dotted.split("."):
        data = data[part]
    return data


def _matches(entry: dict, picks, pairs: list[list[str]]) -> bool:
    if "when" in entry:
        found = picks(entry["when"])
        if not found or any(pick != entry["is"] for pick in found):
            return False
    if not all(any(pick > above for pick in picks(path)) for path, above in entry.get("anyAbove", {}).items()):
        return False
    if not all(any(pick not in values for pick in picks(path)) for path, values in entry.get("anyNotIn", {}).items()):
        return False
    if "mirrored" in entry:
        a, b = entry["mirrored"]
        if not any(a in group and b in group for group in pairs) or [-pick for pick in picks(a)] != picks(b):
            return False
    return True


def derive_label(labels: list[dict], base: dict, values: dict[str, Any], pairs: list[list[str]] = ()) -> str:
    """A condition's label by x-labels: base the task's values (by alias, as JSON), values the condition's set by dotted
    path, pairs its pairs."""

    def picks(path: str) -> list:
        found = values[path] if path in values else _get(base, path)
        return found if isinstance(found, list) else [found]

    for entry in labels:
        unknown = set(entry) - TESTS
        if unknown or "label" not in entry or ("when" in entry) != ("is" in entry):
            raise ValueError(f"x-labels entry {entry} is malformed: needs label, when and is together, and only {', '.join(sorted(TESTS))}")
        if _matches(entry, picks, pairs):
            return entry["label"]
    raise ValueError(f"no x-labels entry matches the condition {values} with pairs {pairs}")
