"""lab_names.yml holds to its own rules, and its JSON copy for rig-setup is the same list."""

import json
import re

from open_rigs import lab_names

PASCAL = re.compile(r"^[A-Z][A-Za-z0-9]*$")
CAMEL = re.compile(r"^[a-z][A-Za-z0-9]*$")


def test_every_rig_names_its_repos_profiles_and_devices():
    names = lab_names.load()
    assert names["rigs"]
    for rig, entry in names["rigs"].items():
        assert entry["repos"] and entry["profiles"], rig
        for kind, instances in entry["devices"].items():
            assert PASCAL.match(kind), (rig, kind)
            assert instances and all(PASCAL.match(i) for i in instances), (rig, kind)


def test_names_follow_the_rules_in_the_header():
    names = lab_names.load()
    columns = dict(names["trialColumns"])
    for rig, entry in names["rigs"].items():
        for setting in entry.get("experimentSettings") or {}:
            assert PASCAL.match(setting), (rig, setting)
        for column in entry.get("trialColumns") or {}:
            assert CAMEL.match(column), (rig, column)
            assert column not in columns, f"{rig}'s {column} is already a lab-wide column"
        assert all("_" not in s and "-" not in s for s in entry.get("streams") or []), rig
    assert all(CAMEL.match(c) for c in columns)
    assert "Seconds" not in columns, "LogData writes Seconds itself"


def test_json_copy_is_the_same_list(tmp_path):
    target = tmp_path / "lab-names.json"
    lab_names.write_json(target)
    assert json.loads(target.read_text(encoding="utf-8")) == lab_names.load()
