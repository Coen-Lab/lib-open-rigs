"""A task schema's x-rules and x-trial-types, evaluated here once for every task that has them.

Both are JSON Schema subschemas checked against a flat object keyed by dotted path:
    x-rules: [{"if": S, "then": T, "message": m} | {"if": S, "unused": [paths], "message": m}, optional "description"]
        on one combination's values (a scalar cell one pick, a list field a list): matching S must match T, else m;
        matching S leaves the unused paths unread.
    x-trial-types: ordered [{"trialType": name, "if": S, "mirrored": [a, b]}], the last with no "if"
        on a condition's values, every value a list; the first entry that matches names it. "mirrored": a and b share
        a pairs group and b's list is a's negated.
"""

from typing import Any

from jsonschema import Draft202012Validator

RULE_KEYS = {"if", "then", "unused", "message", "description"}
TRIAL_TYPE_KEYS = {"trialType", "if", "mirrored"}


def flatten(data: dict, prefix: str = "") -> dict[str, Any]:
    """Every leaf of a task's values (by alias, as JSON) by dotted path; a list is a leaf."""
    flat = {}
    for key, value in data.items():
        if isinstance(value, dict):
            flat.update(flatten(value, f"{prefix}{key}."))
        else:
            flat[prefix + key] = value
    return flat


def _matches(schema: dict, flat: dict) -> bool:
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema).is_valid(flat)


def _applying(rules: list[dict], values: dict):
    flat = flatten(values)
    for rule in rules:
        if set(rule) - RULE_KEYS or not {"if", "message"} <= set(rule) or ("then" in rule) == ("unused" in rule):
            raise ValueError(f"x-rules entry {rule} is malformed: needs if, message and one of then or unused, and only {', '.join(sorted(RULE_KEYS))}")
        if _matches(rule["if"], flat):
            yield rule, flat


def rule_problems(rules: list[dict], values: dict) -> list[str]:
    """The messages of the x-rules one combination's values (by alias, as JSON) break."""
    return [rule["message"] for rule, flat in _applying(rules, values) if "then" in rule and not _matches(rule["then"], flat)]


def unused_paths(rules: list[dict], values: dict) -> set[str]:
    """The paths x-rules say one combination's values (by alias, as JSON) leave unread."""
    return {path for rule, _ in _applying(rules, values) for path in rule.get("unused", [])}


def derive_trial_type(trial_types: list[dict], task: dict, values: dict[str, Any], pairs: list[list[str]] = ()) -> str:
    """A condition's trialType by x-trial-types: task the task's values (by alias, as JSON), values the condition's set by
    dotted path, pairs its pairs."""
    flat = {path: value if isinstance(value, list) else [value] for path, value in {**flatten(task), **values}.items()}
    for i, entry in enumerate(trial_types):
        last = i == len(trial_types) - 1
        if set(entry) - TRIAL_TYPE_KEYS or "trialType" not in entry or ("if" in entry) == last:
            raise ValueError(f"x-trial-types entry {entry} is malformed: needs trialType, if on every entry but the last, and only {', '.join(sorted(TRIAL_TYPE_KEYS))}")
        if "if" in entry and not _matches(entry["if"], flat):
            continue
        if "mirrored" in entry:
            a, b = entry["mirrored"]
            if not any(a in group and b in group for group in pairs) or [-pick for pick in flat[a]] != flat[b]:
                continue
        return entry["trialType"]
    raise ValueError(f"no x-trial-types entry matches the condition {values} with pairs {pairs}")
