"""open_rigs.rigs.trials: the lab's shared trial columns, and one experiment from its files with its checks."""

import csv
import json
import math
import sys

import pytest

from open_rigs.rigs import trials as trials_module
from open_rigs.rigs.trials import CORE_COLUMNS, shared_block_problems, shared_columns, summarise

COLUMNS = ["harpTime", "trialNumber", "trialType", "outcome", "itiStartTime", "itiSeconds", "quietWaitSeconds",
           "trialStartTime", "stimulusOnsetTime", "outcomeTime", "rewardOfferedUl", "rewarded", "penaltySound", "penaltyTimeoutSeconds",
           "penalised", "autoRewarded"]


def _trial(n, start, iti, response, outcome="Correct", timeout=0.0, quiet=0.0, reward=2.0, auto=False, rewarded=None):
    onset = start + iti + quiet
    end = onset + response
    return {"harpTime": end, "trialNumber": n, "trialType": "auditory", "outcome": outcome, "itiStartTime": start,
            "itiSeconds": iti, "quietWaitSeconds": quiet, "trialStartTime": onset - 0.1, "stimulusOnsetTime": onset,
            "outcomeTime": end, "rewardOfferedUl": reward,
            "rewarded": outcome == "Correct" or auto if rewarded is None else rewarded, "penaltySound": timeout > 0,
            "penaltyTimeoutSeconds": timeout, "penalised": timeout > 0, "autoRewarded": auto}


TASK = {"optogenetics": {"laserToStimulusSeconds": 0.1}, "penalty": {"timeoutSeconds": 4.0, "sound": {"enabled": True}}}


def _experiment(tmp_path, trials, rewards, outcome="completed", final=True, columns=COLUMNS, task=TASK):
    folder = tmp_path / "ses-x_2026-11-14T01-00-00"
    (folder / "BonsaiData").mkdir(parents=True)
    if task is not None:
        (folder / "Settings").mkdir()
        (folder / "Settings" / "Settings_Task_2026-11-14T01-00-00.json").write_text(json.dumps({"taskParameters": task}))
    with open(folder / "BonsaiData" / "BonsaiData_Trial_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.DictWriter(f, columns, extrasaction="ignore")
        w.writeheader()
        w.writerows(trials)
    with open(folder / "BonsaiData" / "BonsaiData_Async_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["harpTime", "Event", "Detail", "Value"])
        w.writerows([[r[0], "RewardDelivered", r[2] if len(r) > 2 else "left DO1", r[1]] for r in rewards])
    (folder / "manifest.json").write_text(json.dumps({"run": {"outcome": outcome, "final": final}}))
    return folder


def _chain(n):
    """n consistent trials: each interval starts at the last outcome plus its timeout."""
    trials, start = [], 1000.0
    for i in range(1, n + 1):
        t = _trial(i, start, 2.5, 3.0, outcome="Correct" if i % 2 else "Incorrect", timeout=0.0 if i % 2 else 4.0)
        trials.append(t)
        start = t["outcomeTime"] + t["penaltyTimeoutSeconds"]
    return trials


def test_a_consistent_experiment_has_no_problems(tmp_path):
    trials = _chain(4)
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert (s.outcome, s.trials, s.correct, s.performance, s.responded) == ("completed", 4, 2, 0.5, 1.0)
    assert s.problems == []
    assert s.response_seconds == [3.0] * 4
    assert [round(x, 6) for x in s.interval_seconds] == [2.5, 6.5, 2.5]
    assert (s.requested_ul, s.delivered_ul) == (4.0, 4.0)


def test_a_task_without_quiet_waits_or_optogenetics_is_checked_by_the_core(tmp_path):
    trials = [{**t, "trialStartTime": t["stimulusOnsetTime"]} for t in _chain(4)]  # no lead without optogenetics
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"]
    task = {"penalty": TASK["penalty"]}
    s = summarise(_experiment(tmp_path, trials, rewards, columns=[c for c in COLUMNS if c != "quietWaitSeconds"], task=task))
    assert s.problems == [] and s.trials == 4


def test_a_file_without_outcomeTime_is_one_problem_not_a_crash(tmp_path):
    # Files from before 14 Nov 2026 have no outcomeTime; they are restructured, not read as they are.
    trials = _chain(4)
    s = summarise(_experiment(tmp_path, trials, [], columns=[c for c in COLUMNS if c != "outcomeTime"]))
    assert s.problems == ["BonsaiData_Trial has no outcomeTime column"]


def test_an_auto_rewarded_miss_is_a_rewarded_trial(tmp_path):
    trials = [_trial(1, 1000.0, 2.5, 3.0, outcome="NoResponse", auto=True)]
    s = summarise(_experiment(tmp_path, trials, []))
    assert s.requested_ul == 2.0
    assert s.problems == ["trial 1: rewarded with rewardOfferedUl 2.0 but no RewardDelivered at its outcome"]


def test_an_uncalibrated_line_asks_water_but_delivers_none(tmp_path):
    trials = _chain(2)
    s = summarise(_experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 0.0)]))
    assert (s.requested_ul, s.delivered_ul, s.problems) == (2.0, 0.0, [])


def test_each_inconsistency_is_named(tmp_path):
    trials = _chain(3)
    trials[1]["stimulusOnsetTime"] += 0.01  # the stimulus 10 ms late
    trials[2]["itiStartTime"] += 0.5  # the interval not from the outcome
    trials[2]["stimulusOnsetTime"] += 0.5
    trials[2]["trialNumber"] = 4  # a trial missing
    s = summarise(_experiment(tmp_path, trials, []))  # and no reward logged for trial 1
    text = "\n".join(s.problems)
    assert "trial 2: stimulus" in text
    assert "trial 4: interval started +0.5000 s" in text
    assert "row 3 is trial 4" in text
    assert "trial 1: rewarded with rewardOfferedUl 2.0 but no RewardDelivered" in text


def test_a_trial_without_a_stimulus_is_a_note_and_left_out_of_the_times(tmp_path):
    # stimulusOnsetTime is NaN when the stimulus was never commanded; NaN would pass every tolerance and poison the median.
    trials = _chain(4)
    trials[1]["stimulusOnsetTime"] = float("nan")
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert s.problems == []
    assert s.notes == ["trial 2: stimulusOnsetTime NaN, the stimulus never commanded: left out of the response and interval times"]
    assert s.response_seconds == [3.0] * 3
    assert [round(x, 6) for x in s.interval_seconds] == [6.5, 2.5]


def test_a_nan_or_empty_number_is_a_problem_not_a_pass_or_a_crash(tmp_path, monkeypatch, capsys):
    trials = _chain(4)
    trials[1]["outcomeTime"] = ""  # float("") raised and stopped the command
    trials[2]["itiStartTime"] = float("nan")  # abs(nan - due) > tolerance is False, so it passed
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"]
    folder = _experiment(tmp_path, trials, rewards)
    s = summarise(folder)
    assert s.problems == ["row 2: outcomeTime '' not a number, so the trial is not checked",
                          "row 3: itiStartTime 'nan' not a number, so the trial is not checked"]
    assert s.response_seconds == [3.0, 3.0] and s.interval_seconds == []
    monkeypatch.setattr(sys, "argv", ["summarise-experiment", str(folder)])
    with pytest.raises(SystemExit) as stop:
        trials_module.main()
    assert stop.value.code == 1 and "PROBLEM row 2: outcomeTime ''" in capsys.readouterr().out


def test_an_infinite_number_or_a_reward_value_that_is_not_one_is_a_problem_not_a_crash(tmp_path):
    trials = _chain(3)
    trials[0]["trialNumber"] = "inf"  # int(inf) raised OverflowError and stopped the command
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"] + [(5.0, "2,5")]
    s = summarise(_experiment(tmp_path, trials, rewards))  # float("2,5") raised ValueError and stopped it
    assert s.problems == ["BonsaiData_Async: a RewardDelivered row's Value '2,5' is not a number",
                          "row 1: trialNumber 'inf' not a number, so the trial is not checked"]
    assert s.delivered_ul == 4.0


def test_no_trials_and_no_manifest(tmp_path):
    folder = _experiment(tmp_path, [], [])
    (folder / "manifest.json").unlink()
    s = summarise(folder)
    assert (s.outcome, s.trials) == ("unknown", 0) and math.isnan(s.performance) and s.problems == []


def test_a_run_that_never_closed_says_so(tmp_path):
    s = summarise(_experiment(tmp_path, _chain(1), [], outcome="stopped", final=False))
    assert s.outcome == "stopped (not final: the workflow never finished closing)"


BOTH = ("opto", "quietWait")


def _block(blocks=BOTH):
    return {name: {"type": kind, **({"unit": unit} if unit else {}), "description": description or f"this task's {name}"}
            for name, kind, unit, description in shared_columns(blocks)}


def test_a_task_that_starts_with_the_shared_block_passes():
    columns = {**_block(), "endReason": {"type": "string", "description": "the task's own"}}
    assert shared_block_problems(columns, BOTH) == []


def test_each_block_sits_in_its_place_in_the_core():
    core = [name for name, _, _, _ in CORE_COLUMNS]
    assert [name for name, _, _, _ in shared_columns(())] == core
    both = [name for name, _, _, _ in shared_columns(BOTH)]
    assert both[5:7] == ["itiSeconds", "quietWaitSeconds"] and both[-8:] == [
        "rewardOfferedUl", "rewarded", "penaltySound", "penaltyTimeoutSeconds", "penalised", "laserCondition",
        "laserPowerMw", "laserTriggerTime"]
    assert len(both) == 18 and len(core) == 14


def test_a_task_without_a_block_is_held_to_the_core_alone():
    assert shared_block_problems(_block(()), ()) == []
    # A task naming a block its file lacks, or lacking one it names, is told.
    assert shared_block_problems(_block(()), ("opto",))[0].startswith("the first 17 columns are")
    assert shared_block_problems(_block(BOTH), ())[0].startswith("the first 14 columns are")


def test_an_unknown_block_is_refused():
    with pytest.raises(ValueError, match="no shared block laser; the blocks are quietWait, opto"):
        shared_columns(("laser",))


def test_a_change_to_the_shared_block_is_named():
    columns = _block()
    columns["itiSeconds"]["description"] += " Reworded."
    columns["laserCondition"]["type"] = "double"
    assert shared_block_problems(columns, BOTH) == [
        "itiSeconds's description is not the shared block's (open_rigs.rigs.trials)",
        "laserCondition is double, not int",
    ]
    # Round 3 F12: penaltySound was each task's own words, and two tasks' differed.
    sound = _block()
    sound["penaltySound"]["description"] = "True when the penalty sound played."
    assert shared_block_problems(sound, BOTH) == ["penaltySound's description is not the shared block's (open_rigs.rigs.trials)"]
    reordered = dict(reversed(list(_block().items())))
    assert shared_block_problems(reordered, BOTH)[0].startswith("the first 18 columns are")


def test_go_the_outcomes_and_the_penalty_are_checked_against_the_task(tmp_path):
    # A trial start 5 s off, an outcome that is no outcome, a timeout without the sound and a sound without a timeout were
    # all passed before (audit, 14 Nov 2026); the penalty is held exactly to penalised from 16 Nov 2026.
    trials = _chain(4)
    trials[0]["trialStartTime"] -= 5
    trials[1]["penaltySound"] = False  # penalised, the task's sound on
    trials[2]["outcome"] = "Banana"
    trials[2]["penaltySound"] = True  # not penalised
    trials[3]["penaltyTimeoutSeconds"] = 3.0  # penalised, the task's timeout 4 s
    text = "\n".join(summarise(_experiment(tmp_path, trials, [])).problems)
    assert "trial 1: trial start (trialStartTime) -5.0000 s from its interval's end less the lead (0.1 s) plus quietWaitSeconds" in text
    assert "trial 2: penaltySound False on a trial penalised, with the task's penalty.sound on" in text
    assert "trial 3: outcome 'Banana' is not one of Correct, Incorrect, NoResponse" in text
    assert "trial 3: penaltySound True on a trial not penalised, with the task's penalty.sound on" in text
    assert "trial 4: penaltyTimeoutSeconds 3 on a trial penalised, not 4" in text


def test_a_timeout_on_an_unpenalised_trial_and_the_sound_off_are_named(tmp_path):
    trials = _chain(2)
    trials[0]["penaltyTimeoutSeconds"] = 0.5  # not penalised
    trials[1]["penaltySound"] = True  # penalised, but the task's sound off
    task = {**TASK, "penalty": {"timeoutSeconds": 4.0, "sound": {"enabled": False}}}
    rewards = [(trials[0]["outcomeTime"], 2.0)]
    text = "\n".join(summarise(_experiment(tmp_path, trials, rewards, task=task)).problems)
    assert "trial 1: penaltyTimeoutSeconds 0.5 on a trial not penalised, not 0" in text
    assert "trial 2: penaltySound True on a trial penalised, with the task's penalty.sound off" in text


def test_a_reward_is_checked_both_ways_and_by_its_request(tmp_path):
    # rewarded is the value the reward command came from: a rewarded trial has its RewardDelivered at its outcome,
    # requesting rewardOfferedUl; a trial not rewarded has none there.
    trials = _chain(4)
    trials[2]["rewardOfferedUl"] = 3.0
    rewards = [(trials[0]["outcomeTime"], 1.9, "left DO1 requested=2 pulses=3"),
               (trials[1]["outcomeTime"], 2.0, "left DO1 requested=2 pulses=3"),
               (trials[2]["outcomeTime"], 2.0, "left DO1 requested=2 pulses=3")]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert s.problems == ["trial 2: a RewardDelivered at its outcome, but rewarded is False",
                          "trial 3: rewarded with rewardOfferedUl 3.0 but its RewardDelivered requested 2 ul"]
    assert s.requested_ul == 5.0


def test_a_paid_probe_counts_as_asked_and_an_unpaid_one_offers_without_a_reward(tmp_path):
    trials = _chain(2)
    start = trials[-1]["outcomeTime"] + trials[-1]["penaltyTimeoutSeconds"]
    heads = _trial(3, start, 2.5, 3.0, outcome="Probe", rewarded=True)
    tails = _trial(4, heads["outcomeTime"], 2.5, 3.0, outcome="Probe", timeout=4.0)
    trials += [heads, tails]
    rewards = [(trials[0]["outcomeTime"], 2.0), (heads["outcomeTime"], 2.0)]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert s.problems == [] and s.requested_ul == 4.0


def test_a_negative_quiet_wait_is_named(tmp_path):
    trials = [_trial(1, 1000.0, 2.5, 3.0, quiet=-0.5)]
    s = summarise(_experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)]))
    assert s.problems == ["trial 1: quietWaitSeconds -0.5000 is negative"]


def test_the_laser_trigger_is_checked_when_there_was_one(tmp_path):
    trials = _chain(3)
    trials[0]["laserTriggerTime"] = trials[0]["stimulusOnsetTime"] - 0.1  # on time
    trials[1]["laserTriggerTime"] = float("nan")  # none: no optogenetics, or answered first
    trials[2]["laserTriggerTime"] = trials[2]["stimulusOnsetTime"] - 0.1 + 0.01  # 10 ms late
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"]
    s = summarise(_experiment(tmp_path, trials, rewards, columns=COLUMNS + ["laserTriggerTime"]))
    assert s.problems == ["trial 3: laser trigger +0.0100 s from laserToStimulusSeconds (0.1 s) before the stimulus was due"]


def test_without_the_task_file_its_checks_are_one_problem(tmp_path):
    trials = _chain(2)
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"]
    s = summarise(_experiment(tmp_path, trials, rewards, task=None))
    assert s.problems == ["no readable Settings/Settings_Task_*.json: Go, the laser trigger and the penalty sound not checked"]


def test_performance_is_rig_controls(tmp_path):
    # Correct / (Correct + Incorrect) over two-spout trials; the share answered apart.
    trials = _chain(4) + [_trial(5, 0, 0, 0, outcome="NoResponse")]
    for t, both in zip(trials, [True, True, False, True, True]):
        t["bothSpouts"] = both
    s = summarise(_experiment(tmp_path, trials, [], columns=COLUMNS + ["bothSpouts"]))
    assert (s.correct, round(s.performance, 4), s.responded) == (2, round(1 / 3, 4), 0.8)  # trial 3, one spout, left out


def test_go_no_go_counts_its_one_spout_a_go_miss_as_incorrect_and_responds_on_go_trials(tmp_path):
    # Round 3 F1: go/no-go rows are all bothSpouts False, so performance was NaN, and a withheld NoGo counted as responded.
    trials = _chain(5)
    for t, (outcome, target) in zip(trials, [("Correct", "Go"), ("Correct", "NoGo"), ("Incorrect", "NoGo"),
                                             ("NoResponse", "Go"), ("Probe", "NoReward")]):
        t.update(outcome=outcome, target=target, bothSpouts=False)
    s = summarise(_experiment(tmp_path, trials, [], columns=COLUMNS + ["target", "bothSpouts"]))
    assert (s.performance, s.responded) == (0.5, 0.5)  # 2 of 4 Go/NoGo right; 1 of 2 Go trials licked
    typed = [{"outcome": t["outcome"], "target": t["target"], "bothSpouts": False} for t in trials]
    assert trials_module.performance(typed) == (0.5, 0.5)  # typed rows (open_rigs.trials.read) read the same


def test_a_probe_trial_is_accepted_and_left_out_of_performance_and_the_response_rate(tmp_path):
    trials = _chain(4) + [_trial(5, 0, 0, 0, outcome="Probe")]
    s = summarise(_experiment(tmp_path, trials, []))
    assert not any("not one of" in p for p in s.problems)
    assert (s.trials, s.correct, s.performance, s.responded) == (5, 2, 0.5, 1.0)


def test_videos_are_checked_when_asked(tmp_path):
    trials = _chain(2)
    folder = _experiment(tmp_path, trials, [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "Correct"])
    (folder / "Camera-Pupil").mkdir()
    (folder / "Camera-Pupil" / "Camera-Pupil_2026-11-14T01-00-00.avi").write_bytes(b"RIFF" + (4).to_bytes(4, "little") + b"AVI ")
    (folder / "Camera-Pupil" / "Camera-Pupil_2026-11-14T01-00-00.csv").write_text("harpTime,frameId\n1000,0\n")
    assert summarise(folder).problems == []
    s = summarise(folder, videos=True)
    assert s.videos == ["Camera-Pupil/Camera-Pupil_2026-11-14T01-00-00.avi: 0 frames, CSV 1 rows"]
    assert s.problems == ["Camera-Pupil/Camera-Pupil_2026-11-14T01-00-00.avi: holds 0 frames, but Camera-Pupil_2026-11-14T01-00-00.csv lists 1 "
                          "(1 missing from the video)"]


def _stimulus_state(folder, rows, columns=("harpTime", "trialNumber", "positionCm", "stimulusActive")):
    with open(folder / "BonsaiData" / "BonsaiData_StimulusState_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(columns)
        w.writerows(rows)


def test_a_run_stopped_mid_trial_reports_the_unfinished_trial_and_its_interval(tmp_path):
    trials = _chain(1)
    end = trials[0]["outcomeTime"]
    folder = _experiment(tmp_path, trials, [(end, 2.0)], outcome="stopped")
    _stimulus_state(folder, [[end - 1, 1, 0, True], [end + 1.0, 2, 0, False], [end + 2.19, 2, 0, True], [end + 2.24, 2, 1, True]])
    s = summarise(folder)
    assert s.unfinished == 2 and s.problems == [] and s.notes == []
    assert [round(x, 6) for x in s.interval_seconds] == [2.19]
    first = trials_module.describe(s).splitlines()[0]
    assert first.endswith("stopped, 1 trials, 1 correct, and trial 2 unfinished at the stop")


def test_stimulus_state_of_finished_trials_only_is_no_unfinished_trial(tmp_path):
    trials = _chain(2)
    folder = _experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)])
    _stimulus_state(folder, [[t["stimulusOnsetTime"], t["trialNumber"], 0, True] for t in trials])
    s = summarise(folder)
    assert s.unfinished is None and s.problems == []
    assert [round(x, 6) for x in s.interval_seconds] == [2.5]
    assert "unfinished" not in trials_module.describe(s)


def test_an_unfinished_trial_without_a_stimulus_yet_has_no_interval(tmp_path):
    trials = _chain(1)
    folder = _experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)], outcome="stopped")
    _stimulus_state(folder, [[trials[0]["outcomeTime"] + 1, 2, 0, False]])
    s = summarise(folder)
    assert s.unfinished == 2 and s.interval_seconds == []


def test_a_task_without_a_stimulus_state_log_has_no_unfinished_trial(tmp_path):
    trials = _chain(1)
    s = summarise(_experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)], outcome="stopped"))
    assert s.unfinished is None and s.interval_seconds == [] and s.problems == []


def test_the_c_sharp_helper_fills_the_shared_block_these_name():
    # Audit B5: each task filled the shared block by hand, and two had diverged. OpenRigs.Devices SharedTrialColumns fills
    # every column of it but trialType and outcome, each task's own, and harpTime, the row's time.
    import re
    from pathlib import Path

    source = (Path(__file__).parents[2] / "src" / "OpenRigs.Devices" / "Trials" / "SharedTrialColumns.cs").read_text(encoding="utf-8")
    blocks = {block: re.search(rf"string\[\] {block} = \{{([^}}]*)\}}", source) for block in ("Core", "QuietWait", "Opto")}
    assert all(blocks.values()), blocks
    named = {block: re.findall(r'"(\w+)"', found.group(1)) for block, found in blocks.items() if found}
    task_s_own = {"harpTime", "trialType", "outcome"}
    assert named["Core"] == [c[0] for c in CORE_COLUMNS if c[0] not in task_s_own]
    assert named["QuietWait"] == [c[0] for c in trials_module.QUIET_WAIT_COLUMNS]
    assert named["Opto"] == [c[0] for c in trials_module.OPTO_COLUMNS]
