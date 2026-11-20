"""open_rigs.rigs.trials: the lab's shared trial columns, and one experiment from its files with its checks."""

import csv
import json
import math
import re
import sys
from pathlib import Path

import pytest

from open_rigs.rigs import trials as trials_module
from open_rigs.rigs.trials import CORE_COLUMNS, OUTCOMES, shared_block_problems, shared_columns, summarise

COLUMNS = ["harpTime", "trialNumber", "trialType", "outcome", "itiStartTime", "itiSeconds", "quietWaitSeconds",
           "trialStartTime", "stimulusOnsetTime", "outcomeTime", "rewardOfferedUl", "rewarded",
           "penaltyTimeoutSeconds", "penalised", "autoReward"]


def _trial(n, start, iti, response, outcome="correct", timeout=0.0, quiet=0.0, reward=2.0, auto=False, rewarded=None):
    onset = start + iti + quiet
    end = onset + response
    rewarded = outcome == "correct" or auto if rewarded is None else rewarded
    return {"harpTime": end, "trialNumber": n, "trialType": "auditory", "outcome": outcome, "itiStartTime": start,
            "itiSeconds": iti, "quietWaitSeconds": quiet, "trialStartTime": onset - 0.1, "stimulusOnsetTime": onset,
            "outcomeTime": end, "rewardOfferedUl": reward, "rewarded": rewarded,
            "penaltyTimeoutSeconds": timeout, "penalised": timeout > 0, "autoReward": auto}


def _next(t):
    """The next interval's start: the outcome plus the timeout."""
    return t["outcomeTime"] + t["penaltyTimeoutSeconds"]


TASK = {"optogenetics": {"laserToStimulusSeconds": 0.1}, "penalty": {"incorrect": {"timeoutSeconds": 4.0, "sound": {"levelDbfs": -45}}},
        "reward": {"sizeUl": 2.0}}


def _experiment(tmp_path, trials, rewards, outcome="completed", final=True, columns=COLUMNS, task=TASK):
    folder = tmp_path / "ses-x_2026-11-14T01-00-00"
    (folder / "WorkflowData").mkdir(parents=True)
    if task is not None:
        (folder / "Settings").mkdir()
        (folder / "Settings" / "Settings_Task_2026-11-14T01-00-00.json").write_text(json.dumps({"taskParameters": task}))
    with open(folder / "WorkflowData" / "WorkflowData_Trials_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.DictWriter(f, columns, extrasaction="ignore")
        w.writeheader()
        w.writerows(trials)
    with open(folder / "WorkflowData" / "WorkflowData_Events_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["harpTime", "Event", "Detail", "Value"])
        w.writerows([[r[0], "RewardDelivered", r[2] if len(r) > 2 else "left DO1", r[1]] for r in rewards])
    (folder / "manifest.json").write_text(json.dumps({"run": {"outcome": outcome, "final": final}}))
    return folder


def _chain(n):
    """n consistent trials: each interval starts at the last outcome plus its timeout."""
    trials, start = [], 1000.0
    for i in range(1, n + 1):
        t = _trial(i, start, 2.5, 3.0, outcome="correct" if i % 2 else "incorrect", timeout=0.0 if i % 2 else 4.0)
        trials.append(t)
        start = _next(t)
    return trials


def test_a_consistent_experiment_has_no_problems(tmp_path):
    trials = _chain(4)
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert (s.run_outcome, s.trials, s.correct, s.performance, s.responded) == ("completed", 4, 2, 0.5, 1.0)
    assert s.problems == []
    assert s.response_seconds == [3.0] * 4
    assert [round(x, 6) for x in s.interval_seconds] == [2.5, 6.5, 2.5]
    assert (s.requested_ul, s.delivered_ul) == (4.0, 4.0)


def test_a_task_without_quiet_waits_or_optogenetics_is_checked_by_the_core(tmp_path):
    trials = [{**t, "trialStartTime": t["stimulusOnsetTime"]} for t in _chain(4)]  # no lead without optogenetics
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    task = {"penalty": TASK["penalty"]}
    s = summarise(_experiment(tmp_path, trials, rewards, columns=[c for c in COLUMNS if c != "quietWaitSeconds"], task=task))
    assert s.problems == [] and s.trials == 4


def test_a_negative_spouts_lead_larger_than_the_laser_s_is_the_lead(tmp_path):
    trials = [{**t, "trialStartTime": t["stimulusOnsetTime"] - 0.3} for t in _chain(4)]
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    task = {**TASK, "spouts": {"stimulusToSpoutsInSeconds": -0.3}}
    assert summarise(_experiment(tmp_path, trials, rewards, task=task)).problems == []


def test_a_file_without_outcomeTime_is_one_problem_not_a_crash(tmp_path):
    # Files with no outcomeTime are restructured, not read as they are.
    trials = _chain(4)
    s = summarise(_experiment(tmp_path, trials, [], columns=[c for c in COLUMNS if c != "outcomeTime"]))
    assert s.problems == ["WorkflowData_Trials has no outcomeTime column"]


def test_an_auto_rewarded_miss_is_a_rewarded_trial(tmp_path):
    trials = [_trial(1, 1000.0, 2.5, 3.0, outcome="noResponse", auto=True)]
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
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert s.problems == []
    assert s.notes == ["trial 2: stimulusOnsetTime NaN, the stimulus never commanded: left out of the response and interval times"]
    assert s.response_seconds == [3.0] * 3
    assert [round(x, 6) for x in s.interval_seconds] == [6.5, 2.5]


def test_a_nan_or_empty_number_is_a_problem_not_a_pass_or_a_crash(tmp_path, monkeypatch, capsys):
    trials = _chain(4)
    trials[1]["outcomeTime"] = ""  # float("") raised and stopped the command
    trials[2]["itiStartTime"] = float("nan")  # abs(nan - due) > tolerance is False, so it passed
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
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
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"] + [(5.0, "2,5")]
    s = summarise(_experiment(tmp_path, trials, rewards))  # float("2,5") raised ValueError and stopped it
    assert s.problems == ["WorkflowData_Events: a RewardDelivered row's Value '2,5' is not a number",
                          "row 1: trialNumber 'inf' not a number, so the trial is not checked"]
    assert s.delivered_ul == 4.0


def test_no_trials_and_no_manifest(tmp_path):
    folder = _experiment(tmp_path, [], [])
    (folder / "manifest.json").unlink()
    s = summarise(folder)
    assert (s.run_outcome, s.trials) == ("unknown", 0) and math.isnan(s.performance) and s.problems == []


def test_a_run_that_never_closed_says_so(tmp_path):
    s = summarise(_experiment(tmp_path, _chain(1), [], outcome="stopped", final=False))
    assert s.run_outcome == "stopped (not final: the workflow never finished closing)"


BOTH = ("optogenetics", "quietWait")


def _block(blocks=BOTH):
    return {name: {"type": kind, **({"unit": unit} if unit else {}), "description": description or f"this task's {name}"}
            for name, kind, unit, description in shared_columns(blocks)}


def test_a_task_that_starts_with_the_shared_block_passes():
    columns = {**_block(), "outcomeReason": {"type": "string", "description": "the task's own"}}
    assert shared_block_problems(columns, BOTH) == []


def test_each_block_sits_in_its_place_in_the_core():
    core = [name for name, _, _, _ in CORE_COLUMNS]
    assert [name for name, _, _, _ in shared_columns(())] == core
    both = [name for name, _, _, _ in shared_columns(BOTH)]
    assert both[5:7] == ["itiSeconds", "quietWaitSeconds"] and both[-8:] == [
        "rewardOfferedUl", "rewarded", "penaltyTimeoutSeconds", "penaltyFlashBrightness", "penalised", "laserCondition",
        "laserPowerMw", "laserTriggerTime"]
    assert len(both) == 19 and len(core) == 15


def test_a_conditioned_task_s_blocks_follow_optogenetics_in_order():
    names = [name for name, _, _, _ in shared_columns(("conditions", "autoReward", "stimulus", "optogenetics", "quietWait"))]
    assert names[names.index("penalised"):] == ["penalised", "laserCondition", "laserPowerMw", "laserTriggerTime", "trialConditionId",
                                                "trialCombinationId", "visualInitialContrast", "visualPhaseRad", "auditoryInitialLevelDbfs",
                                                "auditoryInitialAzimuthDeg", "auditoryInitialElevationDeg", "autoReward"]


def test_a_task_without_a_block_is_held_to_the_core_alone():
    assert shared_block_problems(_block(()), ()) == []
    # A task naming a block its file lacks, or lacking one it names, is told.
    assert shared_block_problems(_block(()), ("optogenetics",))[0].startswith("the first 18 columns are")
    assert shared_block_problems(_block(BOTH), ())[0].startswith("the first 15 columns are")


def test_an_unknown_block_is_refused():
    with pytest.raises(ValueError, match="no shared block laser; the blocks are quietWait, trialEnd, optogenetics, conditions, stimulus, autoReward"):
        shared_columns(("laser",))


def test_a_change_to_the_shared_block_is_named():
    columns = _block()
    columns["itiSeconds"]["description"] += " Reworded."
    columns["laserCondition"]["type"] = "double"
    assert shared_block_problems(columns, BOTH) == [
        "itiSeconds's description is not the shared block's (open_rigs.rigs.trials)",
        "laserCondition is double, not int",
    ]
    reordered = dict(reversed(list(_block().items())))
    assert shared_block_problems(reordered, BOTH)[0].startswith("the first 19 columns are")


def test_go_the_outcomes_and_the_penalty_are_checked_against_the_task(tmp_path):
    # A trial start 5 s off and an outcome that is no outcome were passed before; the timeout is held exactly to penalised.
    trials = _chain(4)
    trials[0]["trialStartTime"] -= 5
    trials[2]["outcome"] = "Banana"
    trials[3]["penaltyTimeoutSeconds"] = 3.0  # penalised, the task's timeout 4 s
    text = "\n".join(summarise(_experiment(tmp_path, trials, [])).problems)
    assert "trial 1: trial start (trialStartTime) -5.0000 s from its interval's end less the lead (0.1 s) plus quietWaitSeconds" in text
    assert "trial 3: outcome 'Banana' is not one of correct, incorrect, noResponse, probe, auto" in text
    assert not any("penaltySound" in line for line in text.splitlines())
    assert "trial 4: penaltyTimeoutSeconds 3 on a trial penalised, not 4" in text


def test_a_timeout_on_an_unpenalised_trial_is_named(tmp_path):
    trials = _chain(2)
    trials[0]["penaltyTimeoutSeconds"] = 0.5  # not penalised
    task = {**TASK, "penalty": {"incorrect": {"timeoutSeconds": 4.0, "sound": {"levelDbfs": "mute"}}}}
    rewards = [(trials[0]["outcomeTime"], 2.0)]
    text = "\n".join(summarise(_experiment(tmp_path, trials, rewards, task=task)).problems)
    assert "trial 1: penaltyTimeoutSeconds 0.5 on a trial not penalised, not 0" in text
    assert len(text.splitlines()) == 2  # and the next interval, timed from that timeout; a mute penalty asks nothing of the log


def test_the_flash_brightness_is_held_to_the_penalised_kind_s(tmp_path):
    # A log with penaltyFlashBrightness: a penalised choice its penalty.incorrect.flash.brightness, any other trial 0.
    trials = _chain(4)
    for t in trials:
        t["penaltyFlashBrightness"] = 0.5 if t["penalised"] else 0.0
    task = {**TASK, "penalty": {"incorrect": {"timeoutSeconds": 4.0, "flash": {"brightness": 0.5, "rateHz": 10}}}}
    columns = COLUMNS[:14] + ["penaltyFlashBrightness"] + COLUMNS[14:]
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["rewarded"]]
    assert summarise(_experiment(tmp_path / "a", trials, rewards, columns=columns, task=task)).problems == []
    trials[3]["penaltyFlashBrightness"] = 1.0
    trials[0]["penaltyFlashBrightness"] = 0.25
    text = "\n".join(summarise(_experiment(tmp_path / "b", trials, rewards, columns=columns, task=task)).problems)
    assert "trial 4: penaltyFlashBrightness 1 on a trial penalised, not 0.5" in text
    assert "trial 1: penaltyFlashBrightness 0.25 on a trial not penalised, not 0" in text


def test_a_reward_answered_by_the_board_after_the_outcome_is_at_it(tmp_path):
    # RewardDelivered is the board's answer to the valve's write, 2 to 26 ms after the outcome on a rig (F12)
    trials = _chain(2)
    rewards = [(t["outcomeTime"] + 0.026, 2.0, "left DO1 requested=2 pulses=3") for t in trials if t["outcome"] == "correct"]
    assert summarise(_experiment(tmp_path, trials, rewards)).problems == []
    late = [(t + 0.2, *rest) for t, *rest in rewards]
    assert any("no RewardDelivered at its outcome" in p for p in summarise(_experiment(tmp_path / "late", trials, late)).problems)


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
    heads = _trial(3, _next(trials[-1]), 2.5, 3.0, outcome="probe", rewarded=True)
    tails = _trial(4, _next(heads), 2.5, 3.0, outcome="probe", timeout=4.0)
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
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    s = summarise(_experiment(tmp_path, trials, rewards, columns=COLUMNS + ["laserTriggerTime"]))
    assert s.problems == ["trial 3: laser trigger +0.0100 s from laserToStimulusSeconds (0.1 s) before the stimulus was due"]


def test_without_the_task_file_its_checks_are_one_problem(tmp_path):
    trials = _chain(2)
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    s = summarise(_experiment(tmp_path, trials, rewards, task=None))
    assert s.problems == ["no readable Settings/Settings_Task_*.json: Go, the laser trigger and the timeout not checked"]


def test_performance_is_rig_controls(tmp_path):
    # correct / (correct + incorrect) over trials not offered a single spout; the share answered apart.
    trials = _chain(4) + [_trial(5, 0, 0, 0, outcome="noResponse")]
    for t, single in zip(trials, [False, False, True, False, False]):
        t["singleSpoutOffered"] = single
    s = summarise(_experiment(tmp_path, trials, [], columns=COLUMNS + ["singleSpoutOffered"]))
    assert (s.correct, round(s.performance, 4), s.responded) == (2, round(1 / 3, 4), 0.8)  # trial 3, one spout, left out


def test_go_no_go_counts_its_one_spout_a_go_miss_as_incorrect_and_responds_on_go_trials(tmp_path):
    # Go/no-go rows are all singleSpoutOffered True: still counted, and a withheld noGo is not a response.
    trials = _chain(5)
    for t, (outcome, target) in zip(trials, [("correct", "go"), ("correct", "noGo"), ("incorrect", "noGo"),
                                             ("noResponse", "go"), ("probe", "noReward")]):
        t.update(outcome=outcome, target=target, singleSpoutOffered=True)
    s = summarise(_experiment(tmp_path, trials, [], columns=COLUMNS + ["target", "singleSpoutOffered"]))
    assert (s.performance, s.responded) == (0.5, 0.5)  # 2 of 4 Go/NoGo right; 1 of 2 Go trials licked
    typed = [{"outcome": t["outcome"], "target": t["target"], "singleSpoutOffered": True} for t in trials]
    assert trials_module.performance(typed) == (0.5, 0.5)  # typed rows (open_rigs.trial_log.read) read the same


def test_an_old_auto_rewarded_trial_is_left_out_of_the_response_rate(tmp_path):
    # Navigation before outcome auto: the drift carries the trial into the zone, correct and auto-rewarded, with no
    # movement by the mouse, so it is an auto trial and not asked.
    trials = _chain(4)
    for t in trials:
        t["autoReward"] = True
    s = summarise(_experiment(tmp_path, trials, [], columns=COLUMNS + ["autoReward"]))
    assert s.responded == 1.0  # the two correct (rewarded) trials are the drift's, left out; the two incorrect are the mouse's
    rows = [{"outcome": "correct", "autoReward": "True", "rewarded": "True"},
            {"outcome": "correct", "autoReward": "False", "rewarded": "True"},
            {"outcome": "noResponse", "autoReward": "False", "rewarded": "False"}]
    assert trials_module.performance(rows) == (1.0, 0.5)


def test_outcome_auto_is_left_out_of_the_response_rate_and_performance():
    rows = [{"outcome": "auto", "autoReward": True, "rewarded": True}, {"outcome": "correct"}, {"outcome": "noResponse"}]
    assert trials_module.performance(rows) == (1.0, 0.5)
    # go/no-go: only go trials are asked, and an auto go trial is not one of them
    gng = [{"target": "go", "outcome": "auto", "choice": "noResponse", "autoReward": True, "rewarded": True},
           {"target": "go", "outcome": "correct", "choice": "go"}, {"target": "noGo", "outcome": "correct", "choice": "noResponse"}]
    assert trials_module.performance(gng)[1] == 1.0
    # an old go/no-go log: the auto go trial was noResponse, rewarded, with no choice made
    gng[0]["outcome"] = "noResponse"
    assert trials_module.performance(gng)[1] == 1.0


def test_an_auto_trial_is_responded_when_the_mouse_licked_and_left_out_when_its_licks_are_unknown():
    rows = [{"trialNumber": "1", "outcome": "auto", "autoReward": "True", "rewarded": "True"},
            {"trialNumber": "2", "outcome": "auto", "autoReward": "True", "rewarded": "True"},
            {"trialNumber": "3", "outcome": "correct", "autoReward": "True", "rewarded": "True"},  # old log: auto
            {"trialNumber": "4", "outcome": "noResponse"}]
    assert trials_module.performance(rows)[1] == 0.0  # licks unknown: the three auto trials left out
    assert trials_module.performance(rows, {1: True, 2: False})[1] == 1 / 3  # 1 licked, 2 did not, 3 unknown
    assert trials_module.performance(rows, {1: True, 2: True, 3: True})[1] == 3 / 4
    # go/no-go: an auto go trial with a lick counts; a noGo trial is never asked
    gng = [{"trialNumber": 1, "target": "go", "outcome": "auto", "choice": "noResponse", "autoReward": True, "rewarded": True},
           {"trialNumber": 2, "target": "noGo", "outcome": "correct", "choice": "noResponse"}]
    assert trials_module.performance(gng, {1: True, 2: True})[1] == 1.0
    assert trials_module.performance(gng, {1: False})[1] == 0.0


def test_with_a_choice_column_the_response_is_the_choice():
    def rate(*rows):
        return trials_module.performance(list(rows))[1]
    # a withheld noGo is no response: only the go trial counts, and it was licked
    assert rate({"target": "go", "outcome": "correct", "choice": "left"},
                {"target": "noGo", "outcome": "correct", "choice": "noResponse"}) == 1.0
    # a probe counts in the response rate: a choice is a response, noResponse is none
    assert rate({"outcome": "correct", "choice": "left"}, {"outcome": "probe", "choice": "noResponse"}) == 0.5
    assert rate({"outcome": "correct", "choice": "left"}, {"outcome": "probe", "choice": "right"}) == 1.0
    auto = {"autoReward": True, "rewarded": True, "outcome": "correct"}
    assert rate({**auto, "choice": "noResponse"}, {"outcome": "noResponse", "choice": "noResponse"}) == 0.0  # no choice: auto, left out
    assert rate({**auto, "choice": "left"}, {"outcome": "correct", "choice": "left"}) == 1.0  # chose a side: one
    assert rate({"outcome": "incorrect", "choice": "noResponse"}) == 0.0  # the choice rules, not the outcome


def test_without_a_choice_column_an_old_auto_reward_is_not_asked():
    auto = {"autoReward": True, "rewarded": True, "outcome": "correct"}
    assert trials_module.performance([auto, {"outcome": "correct"}, {"outcome": "noResponse"}])[1] == 1 / 2


def test_a_folder_that_does_not_exist_is_a_problem(tmp_path):
    s = summarise(tmp_path / "ses-NOPE")
    assert s.problems == [f"{tmp_path / 'ses-NOPE'} is not a folder"]


def test_a_probe_trial_is_accepted_and_left_out_of_performance_and_without_a_choice_of_the_response_rate(tmp_path):
    trials = _chain(4) + [_trial(5, 0, 0, 0, outcome="probe")]
    s = summarise(_experiment(tmp_path, trials, []))
    assert not any("not one of" in p for p in s.problems)
    assert (s.trials, s.correct, s.performance, s.responded) == (5, 2, 0.5, 1.0)


def test_videos_are_checked_when_asked(tmp_path):
    trials = _chain(2)
    folder = _experiment(tmp_path, trials, [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"])
    (folder / "Camera-Pupil").mkdir()
    (folder / "Camera-Pupil" / "Camera-Pupil_2026-11-14T01-00-00.avi").write_bytes(b"RIFF" + (4).to_bytes(4, "little") + b"AVI ")
    (folder / "Camera-Pupil" / "Camera-Pupil_2026-11-14T01-00-00.csv").write_text("harpTime,frameId\n1000,0\n")
    assert summarise(folder).problems == []
    s = summarise(folder, videos=True)
    assert s.videos == ["Camera-Pupil/Camera-Pupil_2026-11-14T01-00-00.avi: 0 frames, CSV 1 rows"]
    assert s.problems == ["Camera-Pupil/Camera-Pupil_2026-11-14T01-00-00.avi: holds 0 frames, but Camera-Pupil_2026-11-14T01-00-00.csv lists 1 "
                          "(1 missing from the video)"]


def _stimulus_state(folder, rows, columns=("harpTime", "trialNumber", "positionCm", "auditoryLevelDbfs", "visualContrast")):
    with open(folder / "WorkflowData" / "WorkflowData_StimulusState_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(columns)
        w.writerows(rows)


def test_a_run_stopped_mid_trial_reports_the_unfinished_trial_and_its_interval(tmp_path):
    trials = _chain(1)
    end = trials[0]["outcomeTime"]
    folder = _experiment(tmp_path, trials, [(end, 2.0)], outcome="stopped")
    # visual alone on at 2.19 is the onset: either stimulus on counts
    _stimulus_state(folder, [[end - 1, 1, 0, -40, 1], [end + 1.0, 2, 0, "NaN", 0], [end + 2.19, 2, 0, "NaN", 0.5], [end + 2.24, 2, 1, -40, 0.5]])
    s = summarise(folder)
    assert s.unfinished == 2 and s.problems == [] and s.notes == []
    assert [round(x, 6) for x in s.interval_seconds] == [2.19]
    first = trials_module.describe(s).splitlines()[0]
    assert first.endswith("stopped, 1 trials, 1 correct, and trial 2 unfinished at the stop")


def test_stimulus_state_without_a_stimulus_column_is_a_problem(tmp_path):
    trials = _chain(1)
    folder = _experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)], outcome="stopped")
    _stimulus_state(folder, [[trials[0]["outcomeTime"] + 1, 2, 0]], ("harpTime", "trialNumber", "positionCm"))
    assert summarise(folder).problems == ["WorkflowData_StimulusState lacks harpTime, trialNumber, or a stimulus column "
                                          "(auditoryLevelDbfs, visualContrast): the unfinished trial not looked for"]


def test_stimulus_state_of_finished_trials_only_is_no_unfinished_trial(tmp_path):
    trials = _chain(2)
    folder = _experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)])
    _stimulus_state(folder, [[t["stimulusOnsetTime"], t["trialNumber"], 0, -40, 1] for t in trials])
    s = summarise(folder)
    assert s.unfinished is None and s.problems == []
    assert [round(x, 6) for x in s.interval_seconds] == [2.5]
    assert "unfinished" not in trials_module.describe(s)


def test_an_unfinished_trial_without_a_stimulus_yet_has_no_interval(tmp_path):
    trials = _chain(1)
    folder = _experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)], outcome="stopped")
    _stimulus_state(folder, [[trials[0]["outcomeTime"] + 1, 2, 0, "NaN", 0]])
    s = summarise(folder)
    assert s.unfinished == 2 and s.interval_seconds == []


def test_a_task_without_a_stimulus_state_log_has_no_unfinished_trial(tmp_path):
    trials = _chain(1)
    s = summarise(_experiment(tmp_path, trials, [(trials[0]["outcomeTime"], 2.0)], outcome="stopped"))
    assert s.unfinished is None and s.interval_seconds == [] and s.problems == []


def test_the_c_sharp_helper_fills_the_shared_block_these_name():
    # One filler for every task, so no two tasks diverge: OpenRigs.Trials SharedTrialColumns fills
    # every column of it but trialType and outcome, each task's own, and harpTime, the row's time.
    import re
    from pathlib import Path

    source = (Path(__file__).parents[2] / "src" / "OpenRigs.Trials" / "SharedTrialColumns.cs").read_text(encoding="utf-8")
    blocks = {block: re.search(rf"string\[\] {block} = \{{([^}}]*)\}}", source) for block in ("Core", "QuietWait", "TrialEnd", "Optogenetics")}
    assert all(blocks.values()), blocks
    named = {block: re.findall(r'"(\w+)"', found.group(1)) for block, found in blocks.items() if found}
    task_s_own = {"harpTime", "trialType", "outcome"}
    assert named["Core"] == [c[0] for c in CORE_COLUMNS if c[0] not in task_s_own]
    assert named["QuietWait"] == [c[0] for c in trials_module.QUIET_WAIT_COLUMNS]
    assert named["Optogenetics"] == [c[0] for c in trials_module.OPTOGENETICS_COLUMNS]
    assert named["TrialEnd"] == [c[0] for c in trials_module.TRIAL_END_COLUMNS]


def test_summarise_reads_the_trial_log_as_open_rigs_trial_log_does(tmp_path):
    # summarise had its own reader, whose WorkflowData_Trials*.csv also took WorkflowData_TrialsState files and
    # a last line still being written.
    trials = _chain(2)
    folder = _experiment(tmp_path, trials, [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"])
    (folder / "WorkflowData" / "WorkflowData_TrialsState_2026-11-14T01-00-00.csv").write_text("harpTime,state\n1,x\n")
    with open(folder / "WorkflowData" / "WorkflowData_Trials_2026-11-14T01-00-00.csv", "a") as f:
        f.write("1,3,auditory")  # no newline: still being written
    s = summarise(folder)
    assert (s.trials, s.problems) == (2, [])


def test_the_csharp_outcomes_are_outcomes():
    source = (Path(__file__).parents[2] / "src/OpenRigs.Trials/TrialOutcome.cs").read_text(encoding="utf-8")
    constants = re.findall(r'public const string (\w+) = "(\w+)";', source)
    assert constants == [(o[0].upper() + o[1:], o) for o in OUTCOMES]


def test_summarise_reads_only_chunk_names_and_notes_the_rest(tmp_path):
    trials = _chain(2)
    folder = _experiment(tmp_path, trials, [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"])
    (folder / "WorkflowData" / "WorkflowData_Trials_2026-11-14T01-00-00_copy.csv").write_text("harpTime\nnot a number\n")
    (folder / "Settings" / "Settings_Task_x.json").write_text("{}")
    s = summarise(folder)
    assert (s.trials, s.problems) == (2, [])
    assert s.notes == ["WorkflowData/WorkflowData_Trials_2026-11-14T01-00-00_copy.csv: not read, not named WorkflowData_Trials_<time>.csv",
                       "Settings/Settings_Task_x.json: not read, not named Settings_Task_<time>.json"]


def test_a_penalised_trial_costs_its_kind_s_timeout_and_a_combination_s_value_is_its_trial_s(tmp_path):
    trials, start = [], 1000.0
    for i, (outcome, timeout) in enumerate([("incorrect", 4.0), ("noResponse", 1.0), ("incorrect", 6.0), ("noResponse", 4.0)], 1):
        t = _trial(i, start, 2.5, 3.0, outcome=outcome, timeout=timeout)
        t["trialCombinationId"] = 0 if i < 3 else 1
        trials.append(t)
        start = t["outcomeTime"] + timeout
    task = {**TASK, "penalty": {"incorrect": {"timeoutSeconds": 4.0, "sound": {"levelDbfs": -45}}, "noResponse": {"timeoutSeconds": 1.0, "sound": {"levelDbfs": "mute"}}}}
    folder = _experiment(tmp_path, trials, [], columns=COLUMNS + ["trialCombinationId"], task=task)
    combinations = {"rows": [{"trialCombinationId": 0, "values": {}},
                             {"trialCombinationId": 1, "values": {"penalty.incorrect.timeoutSeconds": 6.0, "penalty.noResponse": None}}]}
    (folder / "Settings" / "Settings_Combinations_2026-11-14T01-00-00.json").write_text(json.dumps(combinations))
    # trial 3 costs its combination's 6 s; trial 4, a penalised miss where its combination never penalises one, is named
    assert summarise(folder).problems == ["trial 4: a penalised miss, but penalty.noResponse is null, so its timeout is not checked"]


def test_a_rewarded_trial_s_interval_starts_at_its_outcome(tmp_path):
    trials = _chain(4)
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    assert trials[1]["itiStartTime"] == trials[0]["outcomeTime"]  # spouts.rewardConsumptionSeconds delays nothing
    late = [dict(t) for t in trials]
    for t in late[1:]:
        for key in ("itiStartTime", "trialStartTime", "stimulusOnsetTime", "outcomeTime", "harpTime"):
            t[key] += 1.5  # an interval that waited a reward's consumption
    text = "\n".join(summarise(_experiment(tmp_path / "late", late, rewards)).problems)
    assert "trial 2: interval started +1.5000 s from the last outcome plus its timeout" in text


def test_an_always_in_trial_has_no_spouts_lead(tmp_path):
    trials = _chain(4)
    for t in trials:
        t["trialStartTime"] = t["stimulusOnsetTime"] - (0.1 if t["trialNumber"] > 2 else 0.3)  # the laser's lead on always-in trials
        t["trialCombinationId"] = 0 if t["trialNumber"] <= 2 else 1
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    task = {**TASK, "spouts": {"stimulusToSpoutsInSeconds": -0.3, "alwaysIn": False}}
    folder = _experiment(tmp_path, trials, rewards, columns=COLUMNS + ["trialCombinationId"], task=task)
    combinations = {"rows": [{"trialCombinationId": 0, "values": {}}, {"trialCombinationId": 1, "values": {"spouts.alwaysIn": True}}]}
    (folder / "Settings" / "Settings_Combinations_2026-11-14T01-00-00.json").write_text(json.dumps(combinations))
    assert summarise(folder).problems == []


def test_the_trial_end_block_follows_outcome_time():
    names = [name for name, _, _, _ in shared_columns(("trialEnd", "quietWait"))]
    assert names[names.index("outcomeTime"):names.index("outcomeTime") + 3] == ["outcomeTime", "trialEndTime", "rewardOfferedUl"]


def _runs_out(n):
    """n trials whose response window runs out 3.5 s after the stimulus: each ends there, an incorrect one then waiting its
    4 s timeout."""
    trials, start = [], 1000.0
    for i in range(1, n + 1):
        t = _trial(i, start, 2.5, 1.0, outcome="correct" if i % 2 else "incorrect", timeout=0.0 if i % 2 else 4.0)
        t["trialEndTime"] = t["stimulusOnsetTime"] + 3.5
        trials.append(t)
        start = t["trialEndTime"] + t["penaltyTimeoutSeconds"]
    return trials


def test_with_the_trial_end_block_each_interval_starts_at_the_last_trial_s_end_plus_its_timeout(tmp_path):
    trials = _runs_out(4)
    rewards = [(t["outcomeTime"], 2.0) for t in trials if t["outcome"] == "correct"]
    columns = COLUMNS + ["trialEndTime"]
    assert summarise(_experiment(tmp_path / "ok", trials, rewards, columns=columns)).problems == []
    # the same rows read without the column: each interval is late on its outcome
    text = "\n".join(summarise(_experiment(tmp_path / "plain", trials, rewards)).problems)
    assert "trial 2: interval started +2.5000 s from the last outcome plus its timeout" in text
    wrong = [dict(t) for t in trials]
    wrong[1]["trialEndTime"] -= 0.5
    wrong[2]["trialEndTime"] = wrong[2]["outcomeTime"] - 0.5
    problems = summarise(_experiment(tmp_path / "wrong", wrong, rewards, columns=columns)).problems
    assert "trial 3: interval started +0.5000 s from the last trial's end (trialEndTime) plus its timeout" in problems
    assert "trial 3: trialEndTime -0.5000 s from its outcome, before it" in problems
