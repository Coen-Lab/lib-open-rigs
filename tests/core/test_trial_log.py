"""open_rigs.trial_log reads an experiment's WorkflowData_Trials file as one typed table, by the
trial columns its manifest carries, or the workflow repository's file."""

import json
import math
import subprocess
from pathlib import Path

import pytest

from open_rigs import trial_log

COLUMNS = {
    "Seconds": {"type": "double", "unit": "s"},
    "trialNumber": {"type": "int"},
    "trialType": {"type": "int", "values": {"0": "Visual", "2": "AVCoherent", "4": "Neutral"}},
    "outcome": {"type": "string", "values": ["Correct", "Incorrect", "NoResponse"]},
    "stimulusOnsetTime": {"type": "double", "unit": "s"},
    "choiceTime": {"type": "double", "unit": "s"},
    "bothSpoutsOffered": {"type": "bool"},
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
          "Seconds,trialNumber,trialType,outcome,stimulusOnsetTime,choiceTime,bothSpoutsOffered,extra\n"
          "12.5,1,2,Correct,10.1,11.0,True,x\n"
          "3600.2,2,4,NoResponse,3598.0,NaN,False,y\n"
          "3610.0,3,0,Inco")
    rows = trial_log.read(tmp_path)
    assert len(rows) == 2
    first, second = rows
    assert first == {"Seconds": 12.5, "trialNumber": 1, "trialType": "AVCoherent", "outcome": "Correct",
                     "stimulusOnsetTime": 10.1, "choiceTime": 11.0, "bothSpoutsOffered": True, "extra": "x"}
    assert second["trialType"] == "Neutral" and second["bothSpoutsOffered"] is False
    assert math.isnan(second["choiceTime"])
    assert trial_log.read(tmp_path, decode=False)[0]["trialType"] == 2


def test_a_cell_of_the_wrong_type_is_named(tmp_path):
    manifest(tmp_path, trial_columns={"columns": COLUMNS})
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv",
          "Seconds,trialNumber,bothSpoutsOffered\n1.0,1,maybe\n")
    with pytest.raises(ValueError, match="line 2, bothSpoutsOffered"):
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


# A task with two conditions: "a" sets contrasts [0.5, 1] (2 combinations) and a reward 3, "b" one combination.
TASK = {"taskParameters": {"reward": {"size": 2}, "contrasts": [0.1, 0.2], "mode": "x", "trialConditions": [
    {"trialType": "a", "weight": 1, "set": {"reward.size": 3}, "pairs": []},
    {"trialType": "b", "weight": 1, "set": {}, "pairs": []}]}}
COMBO_COLUMNS = {"trialCondition": {"type": "int"}, "trialCombination": {"type": "int"}}


def combo_run(tmp_path, rows, task=TASK):
    manifest(tmp_path, trial_columns={"columns": COMBO_COLUMNS})
    if task is not None:
        write(tmp_path / "Settings", "Settings_Task_2026-11-05T10-00-00.json", json.dumps(task))
    write(tmp_path / "WorkflowData", "WorkflowData_Trials_2026-11-05T10-00-00.csv",
          "trialCondition,trialCombination\n" + "".join(f"{c},{k}\n" for c, k in rows))
    return tmp_path


def test_a_combination_decodes_into_columns_of_the_fields_that_varied(tmp_path):
    rows = trial_log.read(combo_run(tmp_path, [(0, 0), (0, 1), (1, 1)]), list_fields=["contrasts"])
    # reward.size differs between the conditions and contrasts within them; mode is the same on every trial.
    assert [(r["reward.size"], r["contrasts"]) for r in rows] == [(3, [0.1]), (3, [0.2]), (2, [0.2])]
    assert "mode" not in rows[0]


def test_an_index_out_of_range_a_missing_task_file_or_list_fields_fail(tmp_path):
    with pytest.raises(ValueError, match="trialCombination 2 is out of range"):
        trial_log.read(combo_run(tmp_path / "a", [(0, 2)]), list_fields=["contrasts"])
    with pytest.raises(ValueError, match="trialCondition 5"):
        trial_log.read(combo_run(tmp_path / "b", [(5, 0)]), list_fields=[])
    with pytest.raises(ValueError, match="0 task files"):
        trial_log.read(combo_run(tmp_path / "c", [(0, 0)], task=None), list_fields=[])
    with pytest.raises(ValueError, match="give repo"):
        trial_log.read(combo_run(tmp_path / "d", [(0, 0)]))


def test_the_list_fields_come_from_the_repo_s_task_schema(tmp_path):
    # read(folder, repo) decodes alone. The params node is the one with x-max-combinations; an x-whole
    # array is never a pick. No commit in the manifest: the clone's schema as it is now.
    params = {"x-max-combinations": 1024, "properties": {"contrasts": {"type": "array"},
                                                          "trialConditions": {"type": "array", "x-whole": True}}}
    repo = tmp_path / "repo"
    write(repo / "src" / "DataSchemas", "x_task.json", json.dumps({"$defs": {"Params": params}, "$ref": "#/$defs/Params"}))
    run = combo_run(tmp_path / "run", [(0, 0), (0, 1), (1, 1)])
    assert trial_log.list_fields_of(run, repo) == ["contrasts"]
    assert [r["contrasts"] for r in trial_log.read(run, repo)] == [[0.1], [0.2], [0.2]]
