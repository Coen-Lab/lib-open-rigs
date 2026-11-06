"""open_rigs.trials joins an experiment's hourly BonsaiData_Trial files into one typed table, by the
trial columns its manifest carries, or the workflow repository's file."""

import json
import math
import subprocess

import pytest

from open_rigs import trials

COLUMNS = {
    "Seconds": {"type": "double", "unit": "s"},
    "trialNumber": {"type": "int"},
    "trialType": {"type": "int", "values": {"0": "Visual", "2": "AVCoherent", "4": "Neutral"}},
    "outcome": {"type": "string", "values": ["Correct", "Incorrect", "NoResponse"]},
    "stimulusOnsetTime": {"type": "double", "unit": "s", "formerly": ["stimulusOnsetSeconds"]},
    "choiceTime": {"type": "double", "unit": "s"},
    "bothSpouts": {"type": "bool", "formerly": ["BothSpouts"]},
}

YAML_TEXT = "trialNumber:\n  type: int\ntrialType:\n  type: int\n  values: {0: Visual, 1: Audio}\n"


def write(folder, name, text):
    folder.mkdir(parents=True, exist_ok=True)
    (folder / name).write_text(text, encoding="utf-8")


def manifest(folder, **fields):
    write(folder, "manifest.json", json.dumps({"version": 2, **fields}))


def test_hour_files_join_typed_with_old_names_and_codes(tmp_path):
    manifest(tmp_path, trial_columns={"path": "x", "sha256": "0", "columns": COLUMNS})
    data = tmp_path / "BonsaiData"
    # The first hour recorded under old names; the second still being written (no newline).
    write(data, "BonsaiData_Trial_2026-11-05T10-00-00.csv",
          "Seconds,trialNumber,trialType,outcome,stimulusOnsetSeconds,BothSpouts,extra\n"
          "12.5,1,2,Correct,10.1,True,x\n")
    write(data, "BonsaiData_Trial_2026-11-05T11-00-00.csv",
          "Seconds,trialNumber,trialType,outcome,stimulusOnsetTime,choiceTime,bothSpouts\n"
          "3600.2,2,4,NoResponse,3598.0,NaN,False\n"
          "3610.0,3,0,Inco")
    rows = trials.read(tmp_path)
    assert len(rows) == 2
    first, second = rows
    assert first == {"Seconds": 12.5, "trialNumber": 1, "trialType": "AVCoherent", "outcome": "Correct",
                     "stimulusOnsetTime": 10.1, "bothSpouts": True, "extra": "x"}
    assert second["trialType"] == "Neutral" and second["bothSpouts"] is False
    assert math.isnan(second["choiceTime"])
    assert trials.read(tmp_path, decode=False)[0]["trialType"] == 2


def test_a_cell_of_the_wrong_type_is_named(tmp_path):
    manifest(tmp_path, trial_columns={"columns": COLUMNS})
    write(tmp_path / "BonsaiData", "BonsaiData_Trial_2026-11-05T10-00-00.csv",
          "Seconds,trialNumber,bothSpouts\n1.0,1,maybe\n")
    with pytest.raises(ValueError, match="line 2, bothSpouts"):
        trials.read(tmp_path)


def test_no_log_is_no_rows(tmp_path):
    assert trials.read(tmp_path) == []


def git(repo, *args):
    subprocess.run(["git", "-C", str(repo), "-c", "user.name=t", "-c", "user.email=t@t", *args], check=True, capture_output=True)


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
    write(experiment / "BonsaiData", "BonsaiData_Trial_2026-11-05T10-00-00.csv", "trialNumber,trialType\n1,1\n")
    assert trials.read(experiment, repo=repo)[0]["trialType"] == "Audio"
    # With no manifest, the repo's current file.
    (experiment / "manifest.json").unlink()
    assert trials.read(experiment, repo=repo)[0]["trialType"] == "Changed"


def test_no_columns_anywhere_is_an_error(tmp_path):
    write(tmp_path / "BonsaiData", "BonsaiData_Trial_2026-11-05T10-00-00.csv", "trialNumber\n1\n")
    with pytest.raises(ValueError, match="no manifest.json"):
        trials.read(tmp_path)
    manifest(tmp_path, trial_columns={"path": "x", "missing": True, "why": "not found"})
    with pytest.raises(ValueError, match="not found"):
        trials.read(tmp_path)
