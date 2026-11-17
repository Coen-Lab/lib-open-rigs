"""The lab's shared trial columns, and one experiment summarised from its files.

Every task's schemas/trial-columns.yml starts with the shared block, in order, with these types, units and descriptions
word for word (each task's own tests hold it to them with shared_block_problems), so one analysis reads every task.
The block is the lab's core (CORE_COLUMNS) with the opt-in blocks the task uses, each in its place: quietWait
(QUIET_WAIT_COLUMNS: quietWaitSeconds) after itiSeconds, optogenetics (OPTOGENETICS_COLUMNS: laserCondition, laserPowerMw,
laserTriggerTime) after penalised. trialType and outcome are in the core by name, type and place, each described by its
task. A task's own columns follow the block.

summarise reads one experiment's WorkflowData/WorkflowData_Trials_<time>.csv, WorkflowData/WorkflowData_Events_<time>.csv (each
RewardDelivered row, Value the microlitres delivered and Detail the microlitres requested=), Settings/Settings_Task_<time>.json
(the lead and the penalty) and manifest.json (how the run ended), and checks the rows against each other and the task. It reads only files named <Name>_<time><suffix>
(open_rigs.chunks); any other file named like one it reads is a NOTE, not read:

- trial numbers run 1, 2, 3 ... with none missing, and each outcome is Correct, Incorrect, NoResponse or Probe;
- each trial's start (trialStartTime) came at its interval's end less the lead, max(laserToStimulusSeconds, 0), plus
  quietWaitSeconds (never negative), within 2 ms;
- each stimulus came itiSeconds plus quietWaitSeconds after its interval started (itiStartTime), within 2 ms;
- each laser trigger (laserTriggerTime, when there was one) came laserToStimulusSeconds before the stimulus was due,
  within 2 ms;
- each interval after the first started at the last trial's outcome plus its penaltyTimeoutSeconds, within 2 ms;
- each penalised trial (penalised) had the penalty sound (penaltySoundOn) exactly when the task's penalty.sound is enabled
  and the task's penalty.timeoutSeconds as its penaltyTimeoutSeconds; an unpenalised one neither;
- each rewarded trial (rewarded) has a RewardDelivered row at its outcome requesting its rewardOfferedUl, and no
  RewardDelivered comes at the outcome of a trial not rewarded.

These checks run on finished trials, the rows of WorkflowData_Trials. A run stopped mid-trial has no row for that trial, but
a task that logs WorkflowData/WorkflowData_StimulusState_*.csv (harpTime, trialNumber, auditoryOn, visualOn) shows it: rows
for the trial after the last finished one are an unfinished trial, reported in the summary (never a problem), and the first
of them with auditoryOn or visualOn True is its stimulus onset, which gives the last outcome's interval to the next stimulus. A task
without that log has no unfinished trial.

A log without quietWaitSeconds has none (0), and a task file without optogenetics a lead of 0.
Performance and the response rate are performance()'s, which says what each counts.

A file without a column the checks read (harpTime, outcomeTime and the rest of the core) is one problem,
not checked further; without a Settings_Task file the checks that need the task are one problem. A trial row with an
empty cell, text, an infinity or NaN in a number column is a problem and its trial is not checked, except a NaN stimulusOnsetTime
(the stimulus never commanded), which is a NOTE, the trial left out of the response and interval times, and a NaN
laserTriggerTime (no trigger), which is not checked. `summarise-experiment <experiment folder> ...` prints each summary,
note and failed check, exiting 1 when a check fails.

With videos (`summarise-experiment --videos`), each camera video is also checked against its CSV, its frames counted
from the file (open_rigs.rigs.video_frames): minutes for a long session, so it is asked for, not done by default. A killed
run's last frame with no CSV row is printed as a NOTE, not a problem (see video_frames).
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
from typing import Any, Dict, Iterable, List, Optional, Tuple

from open_rigs import chunks, trial_log
from open_rigs.rigs import video_frames

# (name, type, unit or None, description or None: None where each task describes it)
Column = Tuple[str, str, Optional[str], Optional[str]]

CORE_COLUMNS: List[Column] = [
    ("harpTime", "double", "s",
     "Harp time the row was complete, when the outcome was known (outcomeTime). The lab's time column, first in every "
     "log (lib-open-rigs specs/logging.md, section 4); a trial's moments are read from their own columns."),
    ("trialNumber", "int", None, "The trial's number in the experiment, from 1."),
    ("trialType", "string", None, None),
    ("outcome", "string", None, None),
    ("itiStartTime", "double", "s",
     "Harp time the inter-trial interval before this trial began: the last trial's outcomeTime plus its "
     "penaltyTimeoutSeconds, exactly; on trial 1 the first Harp clock tick once the trials started."),
    ("itiSeconds", "double", "s",
     "The inter-trial interval drawn for this trial (every trial draws its own), uniformly between "
     "trialStructure.itiMinSeconds and itiMaxSeconds: from itiStartTime to this trial's stimulus with a quiet mouse, "
     "the time to drink included. A task's quiet waits add quietWaitSeconds, so the stimulus was due at "
     "itiStartTime + itiSeconds + quietWaitSeconds (stimulusOnsetTime)."),
    ("trialStartTime", "double", "s",
     "Harp time the trial started: the end of its interval less the lead, max(laserToStimulusSeconds, 0) in the "
     "task file (Settings_Task) and 0 for a task file with no optogenetics block, plus quietWaitSeconds where the task has quiet waits."),
    ("stimulusOnsetTime", "double", "s",
     "Harp time the workflow commanded the stimulus on, NaN if it never was: the first tick of the 1 ms Harp clock at "
     "or after trialStartTime plus the lead, in every experiment. The physical onset is in the hardware logs."),
    ("outcomeTime", "double", "s",
     "Harp time the workflow knew the outcome, the same time as harpTime, as its own column so that every moment of a "
     "trial is named; the reward, the penalty sound and the timeout are timed from it. On a lick it is the Harp clock's "
     "latest tick when the workflow saw the lick, a few ms after it; each lick's own Harp time is in the lick "
     "detectors' logs."),
    ("rewardOfferedUl", "double", "ul",
     "Microlitres a reward on this trial gives, on every trial whether or not it was rewarded: the size the task drew "
     "for it (the task file's rewardSizeUl, or a larger reward where the task has one); 0 on a trial that offers none. "
     "rewarded says whether it was given; what was delivered is RewardDelivered in WorkflowData_Events."),
    ("rewarded", "bool", None,
     "True when the trial's outcome delivered its reward (rewardOfferedUl, at outcomeTime): the value the workflow's "
     "reward command is made from. Which outcomes are rewarded is the task's (its note)."),
    ("penaltySoundOn", "bool", None, "True when the trial was penalised and the task's penalty sound is enabled."),
    ("penaltyTimeoutSeconds", "double", "s",
     "The timeout after this trial: the task's penalty.timeoutSeconds when the trial was penalised, else 0. The next "
     "interval starts after it (itiStartTime)."),
    ("penalised", "bool", None,
     "True when the trial was penalised: the task's penalty timeout (penaltyTimeoutSeconds) and, with its penalty.sound "
     "enabled, the penalty sound (penaltySoundOn) followed its outcome. Which outcomes are penalised is the task's (its note)."),
]

QUIET_WAIT_COLUMNS: List[Column] = [
    ("quietWaitSeconds", "double", "s",
     "How long the quiet waits (lickQuietSeconds, wheelQuietSeconds and wheelQuietCm; OpenRigs.Core QuietWait) "
     "held this trial's start past the end of its interval less the lead; 0 with the waits off or a quiet mouse."),
]

OPTOGENETICS_COLUMNS: List[Column] = [
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

# Each opt-in block by the name a task gives it, with the core column it follows.
BLOCKS: Dict[str, Tuple[str, List[Column]]] = {
    "quietWait": ("itiSeconds", QUIET_WAIT_COLUMNS),
    "optogenetics": ("penalised", OPTOGENETICS_COLUMNS),
}

TIME_COLUMN = "harpTime"
TOLERANCE_S = 0.002  # Harp-clock waits land within a tick or two of 1 ms


def shared_columns(blocks: Iterable[str]) -> List[Column]:
    """The shared block of a task using the opt-in `blocks` (names in BLOCKS): the core with each block in its place."""
    chosen = set(blocks)
    unknown = sorted(chosen - set(BLOCKS))
    if unknown:
        raise ValueError(f"no shared block {', '.join(unknown)}; the blocks are {', '.join(BLOCKS)}")
    columns: List[Column] = []
    for column in CORE_COLUMNS:
        columns.append(column)
        for name, (after, block) in BLOCKS.items():
            if name in chosen and after == column[0]:
                columns += block
    return columns


def shared_block_problems(columns: Dict[str, Any], blocks: Iterable[str]) -> List[str]:
    """What keeps a task's trial columns (its schemas/trial-columns.yml, read as a dict) from starting with the shared
    block of the opt-in `blocks` it uses (names in BLOCKS): each a sentence; none when the block is there, in order,
    with its types, units and descriptions."""
    shared = shared_columns(blocks)
    names = list(columns)
    problems = []
    want = [name for name, _, _, _ in shared]
    if names[: len(want)] != want:
        problems.append(f"the first {len(want)} columns are {names[:len(want)]}, not the shared block {want}")
    for name, kind, unit, description in shared:
        entry = columns.get(name)
        if not isinstance(entry, dict):
            continue
        if entry.get("type") != kind:
            problems.append(f"{name} is {entry.get('type')}, not {kind}")
        if entry.get("unit") != unit:
            problems.append(f"{name}'s unit is {entry.get('unit')}, not {unit}")
        if description is not None and entry.get("description") != description:
            problems.append(f"{name}'s description is not the shared block's (open_rigs.rigs.trials)")
    return problems


OUTCOMES = ("Correct", "Incorrect", "NoResponse", "Probe")
GO_NO_GO = ("Go", "NoGo", "NoReward")  # a go/no-go task's targets: one spout by design, NoReward its probe


def performance(rows: Iterable[Dict[str, Any]]) -> Tuple[float, float]:
    """Performance and the response rate of trial rows (text or typed cells), each NaN with no trial to count.

    Performance is Correct / (Correct + Incorrect) over the trials both spouts were offered (bothSpouts, True where the
    task does not record it) and every go/no-go trial (target Go or NoGo; one spout by design), a Go trial with no lick
    (NoResponse) counted Incorrect. The response rate is the share of trials the mouse responded to: in a go/no-go task
    (a target Go, NoGo or NoReward) the Go trials it licked on, as withholding is a NoGo's answer; else the trials not
    NoResponse, of those not Probe. A Probe (no right answer) counts in neither, though rewarded and penalised say what
    it got."""
    rows = list(rows)
    go_no_go = any(str(r.get("target")) in GO_NO_GO for r in rows)
    correct = counted = 0
    for r in rows:
        target, outcome = str(r.get("target")), r["outcome"]
        go_miss = target == "Go" and outcome == "NoResponse"
        if (_true(r.get("bothSpouts", True)) or target in ("Go", "NoGo")) and (outcome in ("Correct", "Incorrect") or go_miss):
            counted += 1
            correct += outcome == "Correct"
    asked = [r for r in rows if (str(r.get("target")) == "Go" if go_no_go else r["outcome"] != "Probe")]
    answered = sum(r["outcome"] != "NoResponse" for r in asked)
    return (correct / counted if counted else float("nan"), answered / len(asked) if asked else float("nan"))


@dataclass
class Summary:
    folder: str
    outcome: str  # the manifest's run outcome (completed, stopped, ...), "not final" when it never closed; or unknown
    trials: int
    correct: int
    performance: float  # performance()'s first: Correct / (Correct + Incorrect), two-spout and go/no-go trials; NaN with none
    responded: float = float("nan")  # performance()'s second: the share responded to; NaN with none
    response_seconds: List[float] = field(default_factory=list)  # each trial's outcome less its stimulus onset
    interval_seconds: List[float] = field(default_factory=list)  # each outcome to the next stimulus onset
    requested_ul: float = 0.0  # the task's rewards: rewardOfferedUl of each rewarded trial
    delivered_ul: float = 0.0  # every RewardDelivered row's Value (manual rewards included)
    problems: List[str] = field(default_factory=list)
    videos: List[str] = field(default_factory=list)  # each camera video's frames and CSV rows, when checked
    notes: List[str] = field(default_factory=list)  # accepted, not problems: a killed run's video frames with no CSV row
    unfinished: Optional[int] = None  # the number of a trial the run stopped in (StimulusState rows, no Trial row), or None


# The chunked files summarise reads, as (folder, name, suffix): each <name>_<time><suffix> (open_rigs.chunks).
TRIALS_FILES = ("WorkflowData", trial_log.LOG, ".csv")
EVENTS_FILES = ("WorkflowData", "WorkflowData_Events", ".csv")
STIMULUS_FILES = ("WorkflowData", "WorkflowData_StimulusState", ".csv")
TASK_FILES = ("Settings", "Settings_Task", ".json")


def _chunks(folder: Path, files: Tuple[str, str, str]) -> List[Path]:
    return chunks.files(folder / files[0], files[1], files[2])[0]


def skipped(folder: Path) -> List[str]:
    """A note for each file summarise does not read though it is named like one it does: not <Name>_<time><suffix>."""
    return [f"{p.relative_to(folder).as_posix()}: not read, not named {name}_<time>{suffix}"
            for sub, name, suffix in (TRIALS_FILES, EVENTS_FILES, STIMULUS_FILES, TASK_FILES)
            for p in chunks.files(folder / sub, name, suffix)[1]]


def _rows(folder: Path, files: Tuple[str, str, str]) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    for log in _chunks(folder, files):
        with open(log, newline="", encoding="utf-8-sig") as f:
            for row in csv.DictReader(f):
                rows.append(row)
    return rows


def _f(row: Dict[str, str], column: str) -> float:
    return float(row[column])


def _quiet(row: Dict[str, str]) -> float:
    """quietWaitSeconds, 0 in a log without the quietWait block."""
    return _f(row, "quietWaitSeconds") if "quietWaitSeconds" in row else 0.0


def _true(text: Any) -> bool:
    return str(text).strip().lower() == "true"


# The columns summarise reads: of a trial row, and of an Events row.
TRIAL_NEEDS = ("trialNumber", "outcome", "itiStartTime", "itiSeconds", "trialStartTime", "stimulusOnsetTime", "outcomeTime", "rewardOfferedUl", "rewarded", "penaltySoundOn", "penaltyTimeoutSeconds", "penalised")
EVENT_NEEDS = (TIME_COLUMN, "Event", "Value")
# A trial row's numbers, and those of them that may be NaN, by their descriptions above; any other NaN, an infinity, an
# empty cell or text is a problem and the row's checks are skipped.
TRIAL_NUMBERS = ("trialNumber", "itiStartTime", "itiSeconds", "trialStartTime", "stimulusOnsetTime", "outcomeTime",
                 "rewardOfferedUl", "penaltyTimeoutSeconds", "quietWaitSeconds", "laserTriggerTime")
NAN_ALLOWED = ("stimulusOnsetTime", "laserTriggerTime")


def _not_numbers(row: Dict[str, str]) -> List[str]:
    """Each of a trial row's number columns that is not a number it may hold, as `column 'cell'`."""
    bad = []
    for column in TRIAL_NUMBERS:
        if column not in row:
            continue
        try:
            value = float(row[column])
        except (TypeError, ValueError):
            bad.append(f"{column} {row[column]!r}")
            continue
        if math.isinf(value) or (math.isnan(value) and column not in NAN_ALLOWED):
            bad.append(f"{column} {row[column]!r}")
    return bad


def _rewarded(row: Dict[str, str]) -> bool:
    return _true(row["rewarded"])


def _requested(detail: str) -> Optional[float]:
    """A RewardDelivered row's requested microlitres (`requested=` in its Detail), or None without one."""
    for part in str(detail or "").split():
        if part.startswith("requested="):
            try:
                return float(part[len("requested="):])
            except ValueError:
                return None
    return None


@dataclass
class _Task:
    laser_to_stimulus: float  # optogenetics.laserToStimulusSeconds: signed, the trigger before the stimulus
    timeout_seconds: float  # penalty.timeoutSeconds
    sound: bool  # penalty.sound.enabled

    @property
    def lead(self) -> float:
        return max(self.laser_to_stimulus, 0.0)


def _task(folder: Path) -> Optional[_Task]:
    """The experiment's task file values the checks need (Settings/Settings_Task_<time>.json), or None."""
    for path in _chunks(folder, TASK_FILES):
        try:
            p = json.loads(path.read_text(encoding="utf-8-sig"))["taskParameters"]
            return _Task(float((p.get("optogenetics") or {}).get("laserToStimulusSeconds", 0.0)), float(p["penalty"]["timeoutSeconds"]),
                         bool(p["penalty"]["sound"]["enabled"]))
        except (OSError, ValueError, KeyError, TypeError):
            return None
    return None


def summarise(folder: Path, videos: bool = False) -> Summary:
    """One experiment's summary and failed checks, from its files alone; with videos, each camera video against its CSV."""
    folder = Path(folder)
    s = _summarise(folder)
    s.notes += skipped(folder)
    if videos:
        checks = video_frames.check_videos(folder)
        s.videos = [video_frames.describe(check).strip() for check in checks]
        s.problems += [p for check in checks for p in check.problems]
        s.notes += [n for check in checks for n in check.notes] + video_frames.skipped(folder)
    return s


def _summarise(folder: Path) -> Summary:
    trials = trial_log.text_rows(folder)
    events = _rows(folder, EVENTS_FILES)
    try:
        run = json.loads((folder / "manifest.json").read_text(encoding="utf-8-sig"))["run"]
        outcome = (run["outcome"] or "unknown") + (" (not final: the workflow never finished closing)" if run.get("final") is False else "")
    except (OSError, ValueError, KeyError, TypeError):
        outcome = "unknown"
    missing = [f"WorkflowData_Trials has no {c} column" for c in TRIAL_NEEDS if trials and c not in trials[0]]
    missing += [f"WorkflowData_Events has no {c} column" for c in EVENT_NEEDS if events and c not in events[0]]
    if missing:
        return Summary(folder.name, outcome, len(trials), 0, float("nan"), problems=missing)
    correct = sum(r["outcome"] == "Correct" for r in trials)
    s = Summary(folder.name, outcome, len(trials), correct, *performance(trials))
    task = _task(folder)
    if trials and task is None:
        s.problems.append("no readable Settings/Settings_Task_*.json: Go, the laser trigger and the penalty sound not checked")
    rewards = [e for e in events if e["Event"] == "RewardDelivered"]
    reward_times: List[Tuple[float, Optional[float]]] = []  # each RewardDelivered's time and requested microlitres
    for e in rewards:
        try:
            s.delivered_ul += float(e["Value"] or 0)
        except (TypeError, ValueError):
            s.problems.append(f"WorkflowData_Events: a RewardDelivered row's Value {e['Value']!r} is not a number")
        try:
            reward_times.append((_f(e, TIME_COLUMN), _requested(e.get("Detail", ""))))
        except (TypeError, ValueError):
            s.problems.append(f"WorkflowData_Events: a RewardDelivered row's {TIME_COLUMN} {e[TIME_COLUMN]!r} is not a number")
    last: Optional[Dict[str, str]] = None  # the row before, when its numbers could be read
    for n, row in enumerate(trials):
        bad = _not_numbers(row)
        if bad:
            s.problems.append(f"row {n + 1}: {', '.join(bad)} not a number, so the trial is not checked")
            last = None
            continue
        number = int(_f(row, "trialNumber"))
        if number != n + 1:
            s.problems.append(f"row {n + 1} is trial {number}: trial numbers should run 1, 2, 3 ...")
        if row["outcome"] not in OUTCOMES:
            s.problems.append(f"trial {number}: outcome {row['outcome']!r} is not one of {', '.join(OUTCOMES)}")
        onset, end = _f(row, "stimulusOnsetTime"), _f(row, "outcomeTime")
        shown = not math.isnan(onset)
        planned = _f(row, "itiSeconds") + _quiet(row)
        if not shown:
            s.notes.append(f"trial {number}: stimulusOnsetTime NaN, the stimulus never commanded: left out of the "
                           "response and interval times")
        else:
            s.response_seconds.append(end - onset)
            if abs(onset - _f(row, "itiStartTime") - planned) > TOLERANCE_S:
                s.problems.append(f"trial {number}: stimulus {onset - _f(row, 'itiStartTime'):.4f} s after its interval started, "
                                  f"not itiSeconds + quietWaitSeconds = {planned:.4f} s")
        if _quiet(row) < 0:
            s.problems.append(f"trial {number}: quietWaitSeconds {_quiet(row):.4f} is negative")
        if task is not None:
            go = _f(row, "itiStartTime") + planned - task.lead
            if abs(_f(row, "trialStartTime") - go) > TOLERANCE_S:
                s.problems.append(f"trial {number}: trial start (trialStartTime) {_f(row, 'trialStartTime') - go:+.4f} s from its "
                                  f"interval's end less the lead ({task.lead:g} s) plus quietWaitSeconds")
            trigger = _f(row, "laserTriggerTime") if "laserTriggerTime" in row else float("nan")
            due = _f(row, "itiStartTime") + planned - task.laser_to_stimulus
            if not math.isnan(trigger) and abs(trigger - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: laser trigger {trigger - due:+.4f} s from laserToStimulusSeconds "
                                  f"({task.laser_to_stimulus:g} s) before the stimulus was due")
            penalised, sounded, timeout = _true(row["penalised"]), _true(row["penaltySoundOn"]), _f(row, "penaltyTimeoutSeconds")
            if sounded != (penalised and task.sound):
                s.problems.append(f"trial {number}: penaltySoundOn {sounded} on a trial {'penalised' if penalised else 'not penalised'}, "
                                  f"with the task's penalty.sound {'on' if task.sound else 'off'}")
            want = task.timeout_seconds if penalised else 0.0
            if abs(timeout - want) > 1e-9:
                s.problems.append(f"trial {number}: penaltyTimeoutSeconds {timeout:g} on a trial "
                                  f"{'penalised' if penalised else 'not penalised'}, not {want:g}")
        if last is not None:
            due = _f(last, "outcomeTime") + _f(last, "penaltyTimeoutSeconds")
            if abs(_f(row, "itiStartTime") - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: interval started {_f(row, 'itiStartTime') - due:+.4f} s from the last "
                                  "outcome plus its timeout")
            if shown:
                s.interval_seconds.append(onset - _f(last, "outcomeTime"))
        last = row
        at_outcome = [r for t, r in reward_times if abs(t - end) <= TOLERANCE_S]
        offered = _f(row, "rewardOfferedUl")
        if _rewarded(row):
            s.requested_ul += offered
            if not at_outcome:
                nearest = min((t for t, _ in reward_times), key=lambda t: abs(t - end), default=None)
                s.problems.append(f"trial {number}: rewarded with rewardOfferedUl {row['rewardOfferedUl']} but no RewardDelivered at its outcome"
                                  + (f" (the nearest {(nearest - end) * 1000:+.0f} ms from it)" if nearest is not None else ""))
            elif not any(r is None or abs(r - offered) <= 1e-6 for r in at_outcome):
                s.problems.append(f"trial {number}: rewarded with rewardOfferedUl {row['rewardOfferedUl']} but its RewardDelivered "
                                  f"requested {', '.join(f'{r:g}' for r in at_outcome)} ul")
        elif at_outcome:
            s.problems.append(f"trial {number}: a RewardDelivered at its outcome, but rewarded is False")
    _unfinished(s, folder, trials, last)
    return s


def _unfinished(s: Summary, folder: Path, trials: List[Dict[str, str]], last: Optional[Dict[str, str]]) -> None:
    """The trial after the last finished one, when StimulusState has rows for it: the run stopped in it. Its first
    row with auditoryOn or visualOn is the stimulus the last outcome's interval ran to."""
    rows = _rows(folder, STIMULUS_FILES)
    if not rows or not trials:
        return
    if any(c not in rows[0] for c in ("harpTime", "trialNumber", "auditoryOn", "visualOn")):
        s.problems.append("WorkflowData_StimulusState lacks harpTime, trialNumber, auditoryOn or visualOn: the unfinished trial not looked for")
        return
    try:
        cut = int(_f(trials[-1], "trialNumber")) + 1
    except (TypeError, ValueError):
        return
    mine = []
    for r in rows:
        try:
            if int(_f(r, "trialNumber")) == cut:
                mine.append(r)
        except (TypeError, ValueError):
            continue
    if not mine:
        return
    s.unfinished = cut
    if last is None:
        return
    for r in mine:
        try:
            if _true(r["auditoryOn"]) or _true(r["visualOn"]):
                s.interval_seconds.append(_f(r, TIME_COLUMN) - _f(last, "outcomeTime"))
                return
        except (TypeError, ValueError):
            continue


def describe(s: Summary) -> str:
    """The summary as lines of text, each failed check a PROBLEM line."""
    def spread(values: List[float]) -> str:
        return f"median {statistics.median(values):.2f} s (range {min(values):.2f} to {max(values):.2f})" if values else "none"

    def percent(x: float) -> str:
        return "none" if math.isnan(x) else f"{x:.0%}"

    cut = f", and trial {s.unfinished} unfinished at the stop" if s.unfinished is not None else ""
    lines = [f"{s.folder}: {s.outcome}, {s.trials} trials, {s.correct} correct{cut}",
             f"  performance {percent(s.performance)} (Correct / (Correct + Incorrect), two-spout and go/no-go trials),"
             f" responded {percent(s.responded)}",
             f"  response (outcome less stimulus onset): {spread(s.response_seconds)}",
             f"  outcome to next stimulus: {spread(s.interval_seconds)}",
             f"  water: {s.requested_ul:g} ul asked by the task, {s.delivered_ul:g} ul delivered (RewardDelivered)"]
    lines += [f"  video {v}" for v in s.videos]
    lines += [f"  NOTE {n}" for n in s.notes]
    lines += [f"  PROBLEM {p}" for p in s.problems]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="One experiment summarised from its files, with its consistency checks.")
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
