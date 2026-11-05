"""open_rigs.trials joins an experiment's hourly BonsaiData_Trial files into one typed table."""

import math

import pytest

from open_rigs import trials


def write(folder, name, text):
    folder.mkdir(parents=True, exist_ok=True)
    (folder / name).write_text(text, encoding="utf-8")


def test_hour_files_join_typed_with_old_names_and_codes(tmp_path):
    data = tmp_path / "BonsaiData"
    # The first hour recorded under old names; the second still being written (no newline).
    write(data, "BonsaiData_Trial_2026-11-05T10-00-00.csv",
          "Seconds,trialNumber,trialType,outcome,stimulusOnsetSeconds,BothSpouts,extra\n"
          "12.5,1,2,Correct,10.1,True,x\n")
    write(data, "BonsaiData_Trial_2026-11-05T11-00-00.csv",
          "Seconds,trialNumber,trialType,outcome,stimulusOnsetTime,choiceTime,bothSpouts\n"
          "3600.2,2,4,NoResponse,3598.0,NaN,False\n"
          "3610.0,3,0,Inco")
    rows = trials.read(tmp_path, rig="av-dome")
    assert len(rows) == 2
    first, second = rows
    assert first == {"Seconds": 12.5, "trialNumber": 1, "trialType": "AVCoherent", "outcome": "Correct",
                     "stimulusOnsetTime": 10.1, "bothSpouts": True, "extra": "x"}
    assert second["trialType"] == "Neutral" and second["bothSpouts"] is False
    assert math.isnan(second["choiceTime"])
    assert trials.read(tmp_path, decode=False)[0]["trialType"] == 2


def test_a_cell_of_the_wrong_type_is_named(tmp_path):
    write(tmp_path / "BonsaiData", "BonsaiData_Trial_2026-11-05T10-00-00.csv",
          "Seconds,trialNumber,bothSpouts\n1.0,1,maybe\n")
    with pytest.raises(ValueError, match="line 2, bothSpouts"):
        trials.read(tmp_path)


def test_no_log_is_no_rows(tmp_path):
    assert trials.read(tmp_path) == []
