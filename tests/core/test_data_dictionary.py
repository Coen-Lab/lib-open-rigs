"""data_dictionary.yml holds to its own rules."""

import re

from open_rigs import data_dictionary

PASCAL = re.compile(r"^[A-Z][A-Za-z0-9]*$")
CAMEL = re.compile(r"^[a-z][A-Za-z0-9]*$")


def test_every_rig_names_its_repos_profiles_and_devices():
    names = data_dictionary.load()
    assert names["rigs"]
    for rig, entry in names["rigs"].items():
        assert entry["repos"] and entry["profiles"], rig
        for kind, instances in entry["devices"].items():
            assert PASCAL.match(kind), (rig, kind)
            assert instances and all(PASCAL.match(i) for i in instances), (rig, kind)


TYPES = {"int", "double", "bool", "string"}
FIELDS = {"type", "unit", "values", "formerly", "required", "description"}


def check_column(where, name, column):
    assert set(column) <= FIELDS, (where, name, set(column) - FIELDS)
    assert column["type"] in TYPES, (where, name)
    assert column["description"], (where, name)
    if name != "Seconds":
        assert CAMEL.match(name), (where, name)
        # The header's rule: a Harp time ends in Time, a quantity with a unit ends in it.
        assert not name.endswith("Time") or column.get("unit") == "s", (where, name)
        if column.get("unit") == "s" and not name.endswith("Time"):
            assert name.endswith("Seconds"), (where, name)
    values = column.get("values")
    if isinstance(values, dict):
        assert column["type"] == "int", (where, name, "codes are ints")
    elif values is not None:
        assert column["type"] == "string" and all(isinstance(v, str) for v in values), (where, name)
    for old in column.get("formerly") or []:
        assert old != name, (where, name)


def test_names_follow_the_rules_in_the_header():
    names = data_dictionary.load()
    columns = dict(names["trialColumns"])
    for name, column in columns.items():
        check_column("lab", name, column)
    assert {"trialNumber", "outcome", "Seconds"} <= {n for n, c in columns.items() if c.get("required")}
    extras = {}
    for rig, entry in names["rigs"].items():
        for setting in entry.get("experimentSettings") or {}:
            assert PASCAL.match(setting), (rig, setting)
        for name, column in (entry.get("trialColumns") or {}).items():
            check_column(rig, name, column)
            assert not column.get("required"), f"{rig}'s {name}: only lab-wide columns are required"
            assert name not in columns, f"{rig}'s {name} is already a lab-wide column"
            assert name not in extras, f"{rig} and {extras.get(name)} both list {name}: make it lab-wide"
            extras[name] = rig
        assert all("_" not in s and "-" not in s for s in entry.get("streams") or []), rig


def test_no_old_name_is_a_current_one():
    names = data_dictionary.load()
    tables = [names["trialColumns"]] + [e.get("trialColumns") or {} for e in names["rigs"].values()]
    current = {n for t in tables for n in t}
    old = [o for t in tables for c in t.values() for o in c.get("formerly") or []]
    assert not current & set(old), current & set(old)
    assert len(old) == len(set(old)), "one old name maps to two columns"
