"""open_rigs.trial_log reads an experiment's WorkflowData_Trials file as one typed table, by the
trial columns its manifest carries, or the workflow repository's file."""

import json
import math
import re
import subprocess
from pathlib import Path

import pytest

from open_rigs import trial_log

COLUMNS = {
    "Seconds": {"type": "double", "unit": "s"},
    "trialNumber": {"type": "int"},
    "trialType": {"type": "int", "values": {"0": "Visual", "2": "AVCoherent", "4": "Neutral"}},
    "outcome": {"type": "string", "values": ["correct", "incorrect", "noResponse"]},
    "stimulusOnsetTime": {"type": "double", "unit": "s"},
    "choiceTime": {"type": "double", "unit": "s"},
    "singleSpoutOffered": {"type": "bool"},
}

YAML_TEXT = "trialNumber:\n  type: int\ntrialType:\n  type: int\n  values: {0: Visual, 1: Audio}\n"


def write(folder, name, text):
    folder.mkdir(parents=True, exist_ok=True)
    (folder / name).write_text(text, encoding="utf-8")


def manifest(folder, **fields):
    write(folder, "manifest.json", json.dumps({"version": 2, **fields}))


def test_the_file_reads_typed_with_codes(tmp_path):
    manifest(tmp_path, trial_columns={"path": "x", "sha256": "0", "columns": COLUMNS})
    # One file for the experiment, over an hour; its last row is still being written (no newline).
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-14-17.csv",
          "Seconds,trialNumber,trialType,outcome,stimulusOnsetTime,choiceTime,singleSpoutOffered,extra\n"
          "12.5,1,2,correct,10.1,11.0,True,x\n"
          "3600.2,2,4,noResponse,3598.0,NaN,False,y\n"
          "3610.0,3,0,Inco")
    rows = trial_log.read(tmp_path)
    assert len(rows) == 2
    first, second = rows
    assert first == {"Seconds": 12.5, "trialNumber": 1, "trialType": "AVCoherent", "outcome": "correct",
                     "stimulusOnsetTime": 10.1, "choiceTime": 11.0, "singleSpoutOffered": True, "extra": "x"}
    assert second["trialType"] == "Neutral" and second["singleSpoutOffered"] is False
    assert math.isnan(second["choiceTime"])
    assert trial_log.read(tmp_path, decode=False)[0]["trialType"] == 2


def test_a_cell_of_the_wrong_type_is_named(tmp_path):
    manifest(tmp_path, trial_columns={"columns": COLUMNS})
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv",
          "Seconds,trialNumber,singleSpoutOffered\n1.0,1,maybe\n")
    with pytest.raises(ValueError, match="line 2, singleSpoutOffered"):
        trial_log.read(tmp_path)


def test_no_log_is_no_rows(tmp_path):
    assert trial_log.read(tmp_path) == []


def git(repo, *args):
    # No hooks: a machine whose global core.hooksPath asks a person to sign each commit hung this test.
    subprocess.run(["git", "-C", str(repo), "-c", "user.name=t", "-c", "user.email=t@t",
                    "-c", f"core.hooksPath={repo / 'no-hooks'}", *args], check=True, capture_output=True)


def test_an_older_experiment_reads_the_repo_at_its_commit(tmp_path):
    repo = tmp_path / "repo"
    write(repo / "schemas", "trial-columns.yml", YAML_TEXT)
    git(repo, "init", "-q")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "one")
    commit = subprocess.run(["git", "-C", str(repo), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    # The repo's codes change after the experiment ran: the experiment keeps those of its commit.
    write(repo / "schemas", "trial-columns.yml", YAML_TEXT.replace("Audio", "Changed"))
    experiment = tmp_path / "ses"
    manifest(experiment, source={"commit": commit})
    write(experiment / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv", "trialNumber,trialType\n1,1\n")
    assert trial_log.read(experiment, repo=repo)[0]["trialType"] == "Audio"
    # With no manifest, the repo's current file.
    (experiment / "manifest.json").unlink()
    assert trial_log.read(experiment, repo=repo)[0]["trialType"] == "Changed"


def test_no_columns_anywhere_is_an_error(tmp_path):
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv", "trialNumber\n1\n")
    with pytest.raises(ValueError, match="no manifest.json"):
        trial_log.read(tmp_path)
    manifest(tmp_path, trial_columns={"path": "x", "missing": True, "why": "not found"})
    with pytest.raises(ValueError, match="not found"):
        trial_log.read(tmp_path)


MANIFESTS = Path(__file__).parents[2] / "fixtures" / "manifest"


@pytest.mark.parametrize("version", ["v3", "v4"])
@pytest.mark.parametrize("name", ["completed", "running", "no-controller"])
def test_reads_the_writers_own_manifests(tmp_path, version, name):
    write(tmp_path, "manifest.json", (MANIFESTS / version / f"{name}.json").read_text(encoding="utf-8"))
    with pytest.raises(ValueError, match="no trial columns"):
        trial_log.columns(tmp_path)


@pytest.mark.parametrize("version", ["v3", "v4"])
@pytest.mark.parametrize("name", ["no-version", "unknown-version", "unreadable"])
def test_refuses_a_manifest_of_no_known_version(tmp_path, version, name):
    write(tmp_path, "manifest.json", (MANIFESTS / version / "refuse" / f"{name}.json").read_text(encoding="utf-8"))
    with pytest.raises(ValueError) as raised:
        trial_log.columns(tmp_path, repo=tmp_path)
    assert "no trial columns" not in str(raised.value)


def test_only_chunk_names_are_read(tmp_path):
    # A converted experiment keeps other files beside its chunks; a fixture's _x for a time is not one either.
    manifest(tmp_path, trial_columns={"columns": COLUMNS})
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv", "trialNumber\n1\n")
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00_old.csv", "trialNumber\n7\n")
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_x.csv", "trialNumber\n8\n")
    assert trial_log.read(tmp_path) == [{"trialNumber": 1}]


# A run with a combinations table. The manifest carries every scalar as text, as the writer copies it ("0"); the task
# file's NaN is text, the table's bare. contrasts and reward.sizeUl vary; stim.azimuth is constant, a one-entry list.
TABLE_COLUMNS = {
    "trialConditionId": {"type": "int"}, "trialCombinationId": {"type": "int"}, "trialType": {"type": "string"},
    "target": {"type": "string"}, "visualContrast": {"type": "double", "copyOf": "contrasts"},
    "visualAzimuthDeg": {"type": "double", "copyOf": "stim.azimuth", "nanWhen": {"column": "visualContrast", "equals": "0"}},
    "rewardUl": {"type": "double", "copyOf": "reward.sizeUl"}, "delay": {"type": "double", "copyOf": "delaySeconds"},
}
TABLE_TASK = {"taskParameters": {"contrasts": [0, 1], "target": "left", "stim": {"azimuth": [-60]}, "reward": {"sizeUl": 3},
                                 "delaySeconds": "NaN", "trialConditions": [{"weight": 1}, {"weight": 1}]}}
TABLE = {"rows": [
    {"trialCombinationId": 0, "trialConditionId": 0, "trialType": "visual", "conditionWeight": 1, "values": {"contrasts": 0, "reward.sizeUl": 3, "target": "left"}},
    {"trialCombinationId": 1, "trialConditionId": 0, "trialType": "visual", "conditionWeight": 1, "values": {"contrasts": 1, "reward.sizeUl": float("nan"), "target": "left"}},
    {"trialCombinationId": 2, "trialConditionId": 1, "trialType": "auditory", "conditionWeight": 1, "values": {"contrasts": 0, "reward.sizeUl": 2, "target": "right"}},
]}
TABLE_LOG = [  # target is resolved, not a copy: the third trial's centre stays as recorded
    {"trialConditionId": "0", "trialCombinationId": "0", "trialType": "visual", "target": "left", "visualContrast": "0", "visualAzimuthDeg": "NaN", "rewardUl": "3", "delay": "NaN"},
    {"trialConditionId": "0", "trialCombinationId": "1", "trialType": "visual", "target": "left", "visualContrast": "1", "visualAzimuthDeg": "-60", "rewardUl": "NaN", "delay": "NaN"},
    {"trialConditionId": "1", "trialCombinationId": "2", "trialType": "auditory", "target": "centre", "visualContrast": "0", "visualAzimuthDeg": "NaN", "rewardUl": "2", "delay": "NaN"},
]


def table_run(folder, log=TABLE_LOG, table=TABLE, tables=1):
    manifest(folder, trial_columns={"columns": TABLE_COLUMNS})
    write(folder / "Settings", "Settings_Task_2026-11-05T10-00-00.json", json.dumps(TABLE_TASK))
    for n in range(tables):
        write(folder / "Settings", f"Settings_Combinations_2026-11-05T10-00-0{n}.json", json.dumps(table))
    names = list(log[0])
    write(folder / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv",
          ",".join(names) + "\n" + "".join(",".join(row[n] for n in names) + "\n" for row in log))
    return folder


def test_a_combination_s_values_join_as_task_columns_and_never_overwrite(tmp_path):
    rows = trial_log.read(table_run(tmp_path))
    assert [(r["task.contrasts"], r["task.target"], r["target"]) for r in rows] == [(0, "left", "left"), (1, "left", "left"), (0, "right", "centre")]
    assert math.isnan(rows[1]["task.reward.sizeUl"]) and "task.stim.azimuth" not in rows[0]  # constant: in Settings_Task alone
    clash = [{**row, "task.target": "x"} for row in TABLE_LOG]
    with pytest.raises(ValueError, match="recorded column task.target"):
        trial_log.read(table_run(tmp_path / "clash", log=clash))


def test_an_id_needs_exactly_one_table(tmp_path):
    with pytest.raises(ValueError, match="0 files Settings/Settings_Combinations"):
        trial_log.read(table_run(tmp_path / "none", tables=0))
    with pytest.raises(ValueError, match="2 files Settings/Settings_Combinations"):
        trial_log.read(table_run(tmp_path / "two", tables=2))
    with pytest.raises(ValueError, match="trialCombinationId 7 is not a row"):
        trial_log.read(table_run(tmp_path / "out", log=[{**TABLE_LOG[0], "trialCombinationId": "7"}]))


def test_check_fails_on_a_missing_column_or_key_never_skips_it(tmp_path):
    no_column = [{k: v for k, v in row.items() if k != "trialType"} for row in TABLE_LOG]
    with pytest.raises(ValueError, match="trialType: not in the log"):
        trial_log.check(table_run(tmp_path / "column", log=no_column))
    no_key = {"rows": [{k: v for k, v in row.items() if k != "trialConditionId"} for row in TABLE["rows"]]}
    with pytest.raises(ValueError, match="Settings_Combinations row 0 has no trialConditionId"):
        trial_log.check(table_run(tmp_path / "key", table=no_key))


def test_a_log_with_neither_reads_as_written_without_a_table(tmp_path):
    log = [{k: v for k, v in row.items() if k != "trialCombinationId"} for row in TABLE_LOG]
    rows = trial_log.read(table_run(tmp_path, log=log, tables=0))
    assert rows[2]["target"] == "centre" and not any(name.startswith("task.") for name in rows[0])
    trial_log.check(tmp_path)


@pytest.mark.parametrize("trial, column, value, words", [
    (2, "trialConditionId", "0", "trial 3: trialConditionId 0, its combination 2's is 1"),
    (2, "trialType", "visual", "trial 3: trialType 'visual'"),
    (1, "visualContrast", "0.5", "trial 2: visualContrast 0.5, its combination 1's contrasts is 1"),  # the row's value
    (1, "visualAzimuthDeg", "60", "trial 2: visualAzimuthDeg 60.0, its combination 1's stim.azimuth is -60"),  # Settings_Task's, unwrapped
    (0, "visualAzimuthDeg", "-60", "trial 1: visualAzimuthDeg -60.0, not NaN where visualContrast is 0"),  # nanWhen
    (0, "rewardUl", "NaN", "trial 1: rewardUl nan, its combination 0's reward.sizeUl is 3"),
    (1, "rewardUl", "3", "trial 2: rewardUl 3.0"),  # the table's bare NaN
    (0, "delay", "1", "trial 1: delay 1.0, its combination 0's delaySeconds is 'NaN'"),  # Settings_Task's text NaN
])
def test_check_catches_a_planted_mismatch(tmp_path, trial, column, value, words):
    trial_log.check(table_run(tmp_path / "clean"))
    log = [dict(row) for row in TABLE_LOG]
    log[trial][column] = value
    run = table_run(tmp_path / "planted", log=log)
    for call in (trial_log.check, trial_log.read):
        with pytest.raises(ValueError, match=re.escape(words)):
            call(run)


def test_a_level_s_mute_is_logged_as_nan(tmp_path):
    table = {"rows": [{**row, "values": {**row["values"], "sound.levelDbfs": level}} for row, level in zip(TABLE["rows"], ("mute", -40, "mute"))]}
    log = [{**row, "auditoryLevelDbfs": level} for row, level in zip(TABLE_LOG, ("NaN", "-40", "NaN"))]
    run = table_run(tmp_path / "clean", log=log, table=table)
    manifest(run, trial_columns={"columns": {**TABLE_COLUMNS, "auditoryLevelDbfs": {"type": "double", "copyOf": "sound.levelDbfs"}}})
    trial_log.check(run)
    log[0]["auditoryLevelDbfs"] = "-40"
    run = table_run(tmp_path / "planted", log=log, table=table)
    manifest(run, trial_columns={"columns": {**TABLE_COLUMNS, "auditoryLevelDbfs": {"type": "double", "copyOf": "sound.levelDbfs"}}})
    with pytest.raises(ValueError, match="trial 1: auditoryLevelDbfs -40.0, its combination 0's sound.levelDbfs is nan"):
        trial_log.check(run)


def test_copy_of_comes_from_the_manifest_only(tmp_path):
    # Without copyOf in the manifest's columns, only trialConditionId and trialType are held to the table.
    run = table_run(tmp_path, log=[{**TABLE_LOG[1], "visualContrast": "0.5"}])
    manifest(run, trial_columns={"columns": {k: {"type": v["type"]} for k, v in TABLE_COLUMNS.items()}})
    assert trial_log.read(run)[0]["visualContrast"] == 0.5
    with pytest.raises(ValueError, match="copyOf nowhere, a path in neither"):
        manifest(run, trial_columns={"columns": {**TABLE_COLUMNS, "visualContrast": {"type": "double", "copyOf": "nowhere"}}})
        trial_log.check(run)
