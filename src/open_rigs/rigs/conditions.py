"""A task schema's x-rules and x-trial-types, evaluated here once for every task that has them.

Both are JSON Schema subschemas checked against a flat object keyed by dotted path:
    x-rules: [{"if": S, "then": T, "message": m} | {"if": S, "unused": [paths], "message": m}, optional "description"]
        on one combination's values (a scalar cell one pick, a list field a list): matching S must match T, else m;
        matching S leaves the unused paths unread.
    x-trial-types: ordered [{"trialType": name, "if": S, "mirrored": [a, b]}], the last with no "if"
        on a condition's values, every value a list; the first entry that matches names it. "mirrored": a and b share
        a pairs group and b's list is a's negated (a non-number, such as "all", is never mirrored).
    x-required-conditions: [{"if": S, "some": T, "message": m}]
        S on the task's resolved values: when it matches, some combination of a condition with weight > 0 must match T, else m.
"""

import json
from functools import cache
from typing import Any

from jsonschema import Draft202012Validator

RULE_KEYS = {"if", "then", "unused", "message", "description"}
TRIAL_TYPE_KEYS = {"trialType", "if", "mirrored"}
REQUIRED_KEYS = {"if", "some", "message"}
# The most combinations one condition may expand into (a task schema's x-max-combinations, which its C# and the launcher
# read). At 1024 a task's C# check and build at Go took 2.5 s and pydantic's 0.4 s (desk PC, 18 Nov 2026).
MAX_COMBINATIONS = 1024


def flatten(data: dict, prefix: str = "") -> dict[str, Any]:
    """Every leaf of a task's values (by alias, as JSON) by dotted path; a list is a leaf."""
    flat = {}
    for key, value in data.items():
        if isinstance(value, dict):
            flat.update(flatten(value, f"{prefix}{key}."))
        else:
            flat[prefix + key] = value
    return flat


@cache
def _validator(text: str) -> Draft202012Validator:
    schema = json.loads(text)
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def _matches(schema: dict, flat: dict) -> bool:
    return _validator(json.dumps(schema, sort_keys=True)).is_valid(flat)


def _if_matches(entry: dict, flat: dict, kind: str, key: str = "if") -> bool:
    """Whether the entry's if (or key) matches; refused when it names a path not in the values, which would pass vacuously."""
    if missing := [path for path in [*entry[key].get("properties", {}), *entry[key].get("required", [])] if path not in flat]:
        raise ValueError(f"{kind} entry {entry} names {', '.join(dict.fromkeys(missing))}, not in the values")
    return _matches(entry[key], flat)


def _applying(rules: list[dict], values: dict):
    flat = flatten(values)
    for rule in rules:
        if set(rule) - RULE_KEYS or not {"if", "message"} <= set(rule) or ("then" in rule) == ("unused" in rule):
            raise ValueError(f"x-rules entry {rule} is malformed: needs if, message and one of then or unused, and only {', '.join(sorted(RULE_KEYS))}")
        if _if_matches(rule, flat, "x-rules"):
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
        if "if" in entry and not _if_matches(entry, flat, "x-trial-types"):
            continue
        if "mirrored" in entry:
            a, b = entry["mirrored"]
            numbers = all(isinstance(pick, (int, float)) and not isinstance(pick, bool) for pick in flat[a])
            if not any(a in group and b in group for group in pairs) or not numbers or [-pick for pick in flat[a]] != flat[b]:
                continue
        return entry["trialType"]
    raise ValueError(f"no x-trial-types entry matches the condition {values} with pairs {pairs}")


def required_problems(required: list[dict], task: dict, combinations: list[dict]) -> list[str]:
    """The messages of the x-required-conditions the task's values (by alias, as JSON) match but no combination (each one
    combination's values, by alias, as JSON) of a condition with weight > 0 does."""
    flat, combos = flatten(task), [flatten(combo) for combo in combinations]
    problems = []
    for entry in required:
        if set(entry) != REQUIRED_KEYS:
            raise ValueError(f"x-required-conditions entry {entry} is malformed: needs exactly {', '.join(sorted(REQUIRED_KEYS))}")
        if _if_matches(entry, flat, "x-required-conditions") and not any(_if_matches(entry, combo, "x-required-conditions", "some") for combo in combos):
            problems.append(entry["message"])
    return problems


def pick_lists(schema: dict) -> list[str]:
    """The list fields of a task parameters' JSON schema (with its $defs) one entry of which each trial draws: every array
    but an x-whole one, by dotted path, sorted. Each expands into a condition's combinations, one entry each."""
    defs, paths = schema.get("$defs", {}), []

    def walk(node: dict, prefix: str) -> None:
        for key, prop in node.get("properties", {}).items():
            for option in [prop, *prop.get("anyOf", []), *prop.get("allOf", [])]:
                kind = option.get("type")
                if prop.get("x-whole"):
                    break
                if kind == "array" or isinstance(kind, list) and "array" in kind:
                    paths.append(prefix + key)
                    break
                if "$ref" in option:
                    walk(defs[option["$ref"].rsplit("/", 1)[1]], f"{prefix}{key}.")
                    break

    walk(schema, "")
    return sorted(paths)


def _get(data: dict, dotted: str) -> Any:
    for part in dotted.split("."):
        data = data[part]
    return data


def _options(base: dict, values: dict, path: str, list_fields) -> list:
    # A cell's picks: a list field's entries, each a one-entry list; a scalar field's list, else its one value (an x-whole list is one value).
    value = values[path] if path in values else _get(base, path)
    if path in list_fields:
        return [[entry] for entry in value] if isinstance(value, list) else [value]
    return value if isinstance(value, list) and not isinstance(_get(base, path), list) else [value]


def condition_combinations(base: dict, values: dict, pairs: list[list[str]] = (), list_fields=()) -> list[dict[str, Any]]:
    """Every combination of a condition, each a dict of dotted path to value: base the task's values (by alias, as JSON),
    values the condition's set, pairs its pairs, list_fields the task's pick_lists. Each pairs group's lists are zipped
    (equal lengths), then every other list cell of the set and every list field one pick, as a product with the first
    cell's pick changing fastest: the index the trialCombination column logs (the domes' draw uses the same order)."""
    paired = [path for group in pairs for path in group]
    cells: list[tuple[tuple[str, ...], list[tuple]]] = []
    for group in pairs:
        lists = [_options(base, values, path, list_fields) for path in group]
        if len({len(options) for options in lists}) > 1:
            raise ValueError(f"pairs {group} have lists of lengths {', '.join(str(len(options)) for options in lists)}; paired lists must be equally long")
        cells.append((tuple(group), list(zip(*lists))))
    cells += [((path,), [(option,) for option in _options(base, values, path, list_fields)]) for path in sorted({*values, *list_fields} - {*paired})]
    combos: list[dict[str, Any]] = [{}]
    for paths, options in cells:
        if not options:
            raise ValueError(f"{', '.join(paths)} is an empty list")
        combos = [{**combo, **dict(zip(paths, option))} for option in options for combo in combos]
    return combos
