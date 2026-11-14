"""The dome tasks' trial log: the columns every dome task shares, and one experiment summarised from its files.

Every dome task's schemas/trial-columns.yml starts with SHARED_COLUMNS, in that order, with these types, units and
descriptions word for word (each task's own tests hold it to them with shared_block_problems), so one analysis reads
every dome task. trialType, outcome, rewardSizeUl and punishSound are in the block by name, type and place, each
described by its task. A task's own columns follow the block.

summarise reads one experiment's BonsaiData/BonsaiData_Trial_*.csv, BonsaiData/BonsaiData_Async_*.csv (each
RewardDelivered row, Value the microlitres delivered), Settings/Settings_Task_*.json (the lead and the punishment)
and manifest.json (how the run ended), and checks the rows against each other and the task:

- trial numbers run 1, 2, 3 ... with none missing, and each outcome is Correct, Incorrect or NoResponse;
- each Go (trialStartTime) came at its interval's end less the lead, max(laserToStimulusSeconds, 0), plus
  quietWaitSeconds (never negative), within 2 ms;
- each stimulus came itiSeconds plus quietWaitSeconds after its interval started (itiStartTime), within 2 ms;
- each laser trigger (laserTriggerTime, when there was one) came laserToStimulusSeconds before the stimulus was due,
  within 2 ms;
- each interval after the first started at the last trial's outcome plus its punishTimeoutSeconds, within 2 ms;
- the error sound (punishSound) played on each trial with a timeout when the task's punishment.sound is enabled,
  and on no trial without one when the task's timeout is above 0;
- each rewarded trial (rewardSizeUl above 0, and Correct or autoRewarded) has a RewardDelivered row at its outcome.

Performance is Rig Control's: Correct / (Correct + Incorrect), over the trials both
spouts were offered where the task records bothSpouts; the response rate is the trials not NoResponse.

A file without a column the checks read (harpTime, outcomeTime and the rest of the shared block) is one problem,
not checked further; without a Settings_Task file the checks that need the task are one problem. `summarise-experiment
<experiment folder> ...` prints each summary and each failed check, exiting 1 when one fails.

With videos (`summarise-experiment --videos`), each camera video is also checked against its CSV, its frames counted
from the file (open_rigs.rigs.video_frames): minutes for a long session, so it is asked for, not done by default. A killed
run's last second of frames with no CSV row is printed as a NOTE, not a problem (see video_frames).
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from open_rigs.rigs import video_frames

# (name, type, unit or None, description or None: None where each task describes it)
SHARED_COLUMNS: List[Tuple[str, str, Optional[str], Optional[str]]] = [
    ("harpTime", "double", "s",
     "Harp time the row was complete, when the outcome was known (outcomeTime). The lab's time column, first in every "
     "log (lib-open-rigs specs/logging.md, section 4); a trial's moments are read from their own columns."),
    ("trialNumber", "int", None, "The trial's number in the experiment, from 1."),
    ("trialType", "string", None, None),
    ("outcome", "string", None, None),
    ("itiStartTime", "double", "s",
     "Harp time the inter-trial interval before this trial began: the last trial's outcomeTime plus its "
     "punishTimeoutSeconds, exactly; on trial 1 the first Harp clock tick once the trials started."),
    ("itiSeconds", "double", "s",
     "The inter-trial interval drawn for this trial (every trial draws its own), uniformly between "
     "trialStructure.itiMinSeconds and itiMaxSeconds: from itiStartTime to this trial's stimulus with a quiet mouse, "
     "the time to drink included. The stage's quiet waits add quietWaitSeconds, so the stimulus was due at "
     "itiStartTime + itiSeconds + quietWaitSeconds (stimulusOnsetTime)."),
    ("quietWaitSeconds", "double", "s",
     "How long the stage's quiet waits (lickQuietSeconds, wheelQuietSeconds and wheelQuietCm; OpenRigs.Core QuietWait) "
     "held this trial's Go past the end of its interval less the lead; 0 with the waits off or a quiet mouse."),
    ("trialStartTime", "double", "s",
     "Harp time the trial started (Go): the end of its interval less the lead, max(laserToStimulusSeconds, 0) in the "
     "task file (Settings_Task), plus quietWaitSeconds."),
    ("stimulusOnsetTime", "double", "s",
     "Harp time the workflow commanded the stimulus on, NaN if it never was: the first tick of the 1 ms Harp clock at "
     "or after trialStartTime plus the lead, in every experiment. The physical onset is in the hardware logs."),
    ("outcomeTime", "double", "s",
     "Harp time the workflow knew the outcome, the same time as harpTime, as its own column so that every moment of a "
     "trial is named; the reward, the error sound and the timeout are timed from it. On a lick it is the Harp clock's "
     "latest tick when the workflow saw the lick, a few ms after it; each lick's own Harp time is in the lick "
     "detectors' logs."),
    ("rewardSizeUl", "double", "ul", None),
    ("punishSound", "bool", None, None),
    ("punishTimeoutSeconds", "double", "s",
     "The timeout after this trial: the task's punishment.timeoutSeconds when the trial was punished, else 0. The next "
     "interval starts after it (itiStartTime)."),
    ("laserCondition", "int", None,
     "The Zapit condition sent to Zapit this trial (its conditions file's number, from 1), laser on or off: every trial "
     "of an optogenetics experiment has one, so laser on is laserPowerMw > 0, not laserCondition > 0. 0 without "
     "optogenetics."),
    ("laserPowerMw", "double", "mW",
     "Laser power sent to Zapit for this trial (the task's optogenetics.laserPowerMw), as commanded, not measured. 0 on "
     "laser-off trials, without optogenetics, and on a trial answered before its trigger."),
    ("laserTriggerTime", "double", "s",
     "Harp time the workflow raised the Zapit trigger line (the laser, or at 0 mW the galvos and masking light alone): "
     "the first tick of the 1 ms Harp clock at or after laserToStimulusSeconds before the stimulus was due. NaN without "
     "optogenetics and on a trial that ended before its trigger, so a laser-off trial (a time, laserPowerMw 0) and one "
     "answered first (NaN) differ."),
]

TIME_COLUMN = "harpTime"
TOLERANCE_S = 0.002  # Harp-clock waits land within a tick or two of 1 ms


def shared_block_problems(columns: Dict[str, Any]) -> List[str]:
    """What keeps a task's trial columns (its schemas/trial-columns.yml, read as a dict) from starting with the shared
    block: each a sentence; none when the block is there, in order, with its types, units and descriptions."""
    names = list(columns)
    problems = []
    want = [name for name, _, _, _ in SHARED_COLUMNS]
    if names[: len(want)] != want:
        problems.append(f"the first {len(want)} columns are {names[:len(want)]}, not the shared block {want}")
    for name, kind, unit, description in SHARED_COLUMNS:
        entry = columns.get(name)
        if not isinstance(entry, dict):
            continue
        if entry.get("type") != kind:
            problems.append(f"{name} is {entry.get('type')}, not {kind}")
        if entry.get("unit") != unit:
            problems.append(f"{name}'s unit is {entry.get('unit')}, not {unit}")
        if description is not None and entry.get("description") != description:
            problems.append(f"{name}'s description is not the shared block's (open_rigs.rigs.dome_trials.SHARED_COLUMNS)")
    return problems


OUTCOMES = ("Correct", "Incorrect", "NoResponse")


@dataclass
class Summary:
    folder: str
    outcome: str  # the manifest's run outcome (completed, stopped, ...), "not final" when it never closed; or unknown
    trials: int
    correct: int
    performance: float  # Rig Control's: Correct / (Correct + Incorrect), two-spout trials where recorded; NaN with none
    responded: float = float("nan")  # the trials not NoResponse, of all; NaN with no trials
    response_seconds: List[float] = field(default_factory=list)  # each trial's outcome less its stimulus onset
    interval_seconds: List[float] = field(default_factory=list)  # each outcome to the next stimulus onset
    requested_ul: float = 0.0  # the task's rewards: rewardSizeUl of each rewarded trial
    delivered_ul: float = 0.0  # every RewardDelivered row's Value (manual rewards included)
    problems: List[str] = field(default_factory=list)
    videos: List[str] = field(default_factory=list)  # each camera video's frames and CSV rows, when checked
    notes: List[str] = field(default_factory=list)  # accepted, not problems: a killed run's video frames with no CSV row


def _rows(folder: Path, pattern: str) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    for log in sorted(folder.glob(pattern)):
        with open(log, newline="", encoding="utf-8-sig") as f:
            for row in csv.DictReader(f):
                rows.append(row)
    return rows


def _f(row: Dict[str, str], column: str) -> float:
    return float(row[column])


def _true(text: Any) -> bool:
    return str(text).strip().lower() == "true"


# The columns summarise reads: of a trial row, and of an async row.
TRIAL_NEEDS = ("trialNumber", "outcome", "itiStartTime", "itiSeconds", "quietWaitSeconds", "trialStartTime",
               "stimulusOnsetTime", "outcomeTime", "rewardSizeUl", "punishSound", "punishTimeoutSeconds")
ASYNC_NEEDS = (TIME_COLUMN, "Event", "Value")


def _rewarded(row: Dict[str, str]) -> bool:
    return _f(row, "rewardSizeUl") > 0 and (row["outcome"] == "Correct" or _true(row.get("autoRewarded", "")))


@dataclass
class _Task:
    laser_to_stimulus: float  # optogenetics.laserToStimulusSeconds: signed, the trigger before the stimulus
    timeout_seconds: float  # punishment.timeoutSeconds
    sound: bool  # punishment.sound.enabled

    @property
    def lead(self) -> float:
        return max(self.laser_to_stimulus, 0.0)


def _task(folder: Path) -> Optional[_Task]:
    """The experiment's task file values the checks need (Settings/Settings_Task_*.json), or None."""
    for path in sorted(folder.glob("Settings/Settings_Task_*.json")):
        try:
            p = json.loads(path.read_text(encoding="utf-8-sig"))["taskParameters"]
            return _Task(float(p["optogenetics"]["laserToStimulusSeconds"]), float(p["punishment"]["timeoutSeconds"]),
                         bool(p["punishment"]["sound"]["enabled"]))
        except (OSError, ValueError, KeyError, TypeError):
            return None
    return None


def summarise(folder: Path, videos: bool = False) -> Summary:
    """One experiment's summary and failed checks, from its files alone; with videos, each camera video against its CSV."""
    folder = Path(folder)
    s = _summarise(folder)
    if videos:
        checks = video_frames.check_videos(folder)
        s.videos = [video_frames.describe(check).strip() for check in checks]
        s.problems += [p for check in checks for p in check.problems]
        s.notes += [n for check in checks for n in check.notes]
    return s


def _summarise(folder: Path) -> Summary:
    trials = _rows(folder, "BonsaiData/BonsaiData_Trial*.csv")
    events = _rows(folder, "BonsaiData/BonsaiData_Async*.csv")
    try:
        run = json.loads((folder / "manifest.json").read_text(encoding="utf-8-sig"))["run"]
        outcome = (run["outcome"] or "unknown") + (" (not final: the workflow never finished closing)" if run.get("final") is False else "")
    except (OSError, ValueError, KeyError, TypeError):
        outcome = "unknown"
    missing = [f"BonsaiData_Trial has no {c} column" for c in TRIAL_NEEDS if trials and c not in trials[0]]
    missing += [f"BonsaiData_Async has no {c} column" for c in ASYNC_NEEDS if events and c not in events[0]]
    if missing:
        return Summary(folder.name, outcome, len(trials), 0, float("nan"), problems=missing)
    correct = sum(r["outcome"] == "Correct" for r in trials)
    counted = [r for r in trials if r["outcome"] in ("Correct", "Incorrect") and _true(r.get("bothSpouts", "True"))]
    s = Summary(folder.name, outcome, len(trials), correct,
                sum(r["outcome"] == "Correct" for r in counted) / len(counted) if counted else float("nan"),
                sum(r["outcome"] != "NoResponse" for r in trials) / len(trials) if trials else float("nan"))
    task = _task(folder)
    if trials and task is None:
        s.problems.append("no readable Settings/Settings_Task_*.json: Go, the laser trigger and the error sound not checked")
    rewards = [e for e in events if e["Event"] == "RewardDelivered"]
    s.delivered_ul = sum(float(e["Value"] or 0) for e in rewards)
    reward_times = [_f(e, TIME_COLUMN) for e in rewards]
    for n, row in enumerate(trials):
        number = int(row["trialNumber"])
        if number != n + 1:
            s.problems.append(f"row {n + 1} is trial {number}: trial numbers should run 1, 2, 3 ...")
        if row["outcome"] not in OUTCOMES:
            s.problems.append(f"trial {number}: outcome {row['outcome']!r} is not one of {', '.join(OUTCOMES)}")
        onset, end = _f(row, "stimulusOnsetTime"), _f(row, "outcomeTime")
        s.response_seconds.append(end - onset)
        planned = _f(row, "itiSeconds") + _f(row, "quietWaitSeconds")
        if abs(onset - _f(row, "itiStartTime") - planned) > TOLERANCE_S:
            s.problems.append(f"trial {number}: stimulus {onset - _f(row, 'itiStartTime'):.4f} s after its interval started, "
                              f"not itiSeconds + quietWaitSeconds = {planned:.4f} s")
        if _f(row, "quietWaitSeconds") < 0:
            s.problems.append(f"trial {number}: quietWaitSeconds {_f(row, 'quietWaitSeconds'):.4f} is negative")
        if task is not None:
            go = _f(row, "itiStartTime") + planned - task.lead
            if abs(_f(row, "trialStartTime") - go) > TOLERANCE_S:
                s.problems.append(f"trial {number}: Go (trialStartTime) {_f(row, 'trialStartTime') - go:+.4f} s from its "
                                  f"interval's end less the lead ({task.lead:g} s) plus quietWaitSeconds")
            trigger = _f(row, "laserTriggerTime") if "laserTriggerTime" in row else float("nan")
            due = _f(row, "itiStartTime") + planned - task.laser_to_stimulus
            if not math.isnan(trigger) and abs(trigger - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: laser trigger {trigger - due:+.4f} s from laserToStimulusSeconds "
                                  f"({task.laser_to_stimulus:g} s) before the stimulus was due")
            sounded, timed_out = _true(row["punishSound"]), _f(row, "punishTimeoutSeconds") > 0
            if timed_out and sounded != task.sound:
                s.problems.append(f"trial {number}: a timeout {'with' if sounded else 'without'} the error sound, but the "
                                  f"task's punishment.sound is {'off' if sounded else 'on'}")
            if not timed_out and sounded and task.timeout_seconds > 0:
                s.problems.append(f"trial {number}: the error sound with no timeout, though the task's timeout is "
                                  f"{task.timeout_seconds:g} s")
        if n:
            last = trials[n - 1]
            due = _f(last, "outcomeTime") + _f(last, "punishTimeoutSeconds")
            if abs(_f(row, "itiStartTime") - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: interval started {_f(row, 'itiStartTime') - due:+.4f} s from the last "
                                  "outcome plus its timeout")
            s.interval_seconds.append(onset - _f(last, "outcomeTime"))
        if _rewarded(row):
            s.requested_ul += _f(row, "rewardSizeUl")
            if not any(abs(t - end) <= TOLERANCE_S for t in reward_times):
                nearest = min(reward_times, key=lambda t: abs(t - end), default=None)
                s.problems.append(f"trial {number}: rewarded with rewardSizeUl {row['rewardSizeUl']} but no RewardDelivered at its outcome"
                                  + (f" (the nearest {(nearest - end) * 1000:+.0f} ms from it)" if nearest is not None else ""))
    return s


def describe(s: Summary) -> str:
    """The summary as lines of text, each failed check a PROBLEM line."""
    def spread(values: List[float]) -> str:
        return f"median {statistics.median(values):.2f} s (range {min(values):.2f} to {max(values):.2f})" if values else "none"

    def percent(x: float) -> str:
        return "none" if math.isnan(x) else f"{x:.0%}"

    lines = [f"{s.folder}: {s.outcome}, {s.trials} trials, {s.correct} correct",
             f"  performance {percent(s.performance)} (Correct / (Correct + Incorrect), two-spout trials, as Rig Control),"
             f" responded {percent(s.responded)}",
             f"  response (outcome less stimulus onset): {spread(s.response_seconds)}",
             f"  outcome to next stimulus: {spread(s.interval_seconds)}",
             f"  water: {s.requested_ul:g} ul asked by the task, {s.delivered_ul:g} ul delivered (RewardDelivered)"]
    lines += [f"  video {v}" for v in s.videos]
    lines += [f"  NOTE {n}" for n in s.notes]
    lines += [f"  PROBLEM {p}" for p in s.problems]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="One dome experiment summarised from its files, with its consistency checks.")
    parser.add_argument("folders", nargs="+", type=Path, help="experiment folders (ses-...)")
    parser.add_argument("--videos", action="store_true", help="also count each camera video's frames against its CSV (minutes)")
    args = parser.parse_args()
    failed = False
    for folder in args.folders:
        s = summarise(folder, args.videos)
        print(describe(s))
        failed |= bool(s.problems)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
