"""The lab's shared trial columns, and one experiment summarised from its files.

Every task's schemas/trial-columns.yml starts with the shared block, in order, with these types, units and descriptions
word for word (each task's own tests hold it to them with shared_block_problems), so one analysis reads every task.
The block is the lab's core (CORE_COLUMNS) with the opt-in blocks the task uses, each in its place: quietWait
(QUIET_WAIT_COLUMNS: quietWaitSeconds) after itiSeconds, trialEnd (TRIAL_END_COLUMNS: trialEndTime, for a task whose
response window always runs in full) after outcomeTime, optogenetics (OPTOGENETICS_COLUMNS: laserCondition, laserPointsMm,
laserPowerMw, laserTriggerTime) after penalised, then a conditioned task's conditions (CONDITIONS_COLUMNS), stimulus (STIMULUS_COLUMNS)
and autoReward (AUTO_REWARD_COLUMNS) blocks, in that order. trialType and outcome are in the core by name, type and place,
each described by its task. A task's own columns follow the block.

summarise reads one experiment's WorkflowData/WorkflowData_Trials_<time>.csv, WorkflowData/WorkflowData_Events_<time>.csv (each
RewardDelivered row, Value the microlitres delivered and Detail the microlitres requested=), Settings/Settings_Task_<time>.json
(the lead and the penalty) and manifest.json (how the run ended), and checks the rows against each other and the task. It reads only files named <Name>_<time><suffix>
(open_rigs.chunks); any other file named like one it reads is a NOTE, not read:

- trial numbers run 1, 2, 3 ... with none missing, and each outcome is correct, incorrect, noResponse, probe or auto;
- each trial's start (trialStartTime) came at its interval's end less the lead, max(laserToStimulusSeconds, -spouts.stimulusToSpoutsInSeconds
  unless spouts.alwaysIn, 0), its combination's values where it has them, plus quietWaitSeconds (never negative), within 2 ms;
- each stimulus came itiSeconds plus quietWaitSeconds after its interval started (itiStartTime), within 2 ms;
- each laser trigger (laserTriggerTime, when there was one) came laserToStimulusSeconds before the stimulus was due,
  within 2 ms;
- each interval after the first started at the last trial's outcome (its trialEndTime in a log with the trialEnd block,
  never before its outcome) plus its penaltyTimeoutSeconds, within 2 ms;
- each penalised trial (penalised) had its kind's timeout as its penaltyTimeoutSeconds (penalty.incorrect.timeoutSeconds, or on a miss
  penalty.noResponse.timeoutSeconds), an unpenalised one 0, and, in a log with penaltyFlashBrightness, its kind's
  flash.brightness as that (0 unpenalised);
- each rewarded trial (rewarded) has a RewardDelivered row at its outcome (from 2 ms before it to REWARD_ANSWER_S after:
  the row is the board's answer to the valve's write) requesting its rewardOfferedUl, and no RewardDelivered comes at the
  outcome of a trial not rewarded.

These checks run on finished trials, the rows of WorkflowData_Trials. A run stopped mid-trial has no row for that trial, but
a task that logs WorkflowData/WorkflowData_StimulusState_*.csv (harpTime, trialNumber, and auditoryLevelDbfs,
visualContrast or both: StimulusState's instantaneous columns, not the trial log's auditoryInitialLevelDbfs and
visualInitialContrast) shows it: rows for the trial after the last finished one are an
unfinished trial, reported in the summary (never a problem), and the first of them with a stimulus on (auditoryLevelDbfs
not NaN, or visualContrast above 0) is its stimulus onset, which gives the last outcome's interval
to the next stimulus. A task without that log has no unfinished trial.

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
from open_rigs.core.task import PENALTY_TIMEOUT_SECONDS
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
     "Harp time the inter-trial interval before this trial began: the last trial's trialEndTime (its "
     "outcomeTime in a log without trialEndTime) plus its penaltyTimeoutSeconds, exactly; on trial 1 the first Harp clock tick once the trials started."),
    ("itiSeconds", "double", "s",
     "The inter-trial interval drawn for this trial (every trial draws its own), uniformly between "
     "trialStructure.itiMinSeconds and itiMaxSeconds: from itiStartTime to this trial's stimulus with a quiet mouse, "
     "the time to drink included. A task's quiet waits add quietWaitSeconds, so the stimulus was due at "
     "itiStartTime + itiSeconds + quietWaitSeconds (stimulusOnsetTime)."),
    ("trialStartTime", "double", "s",
     "Harp time the trial started: the end of its interval less the lead, max(laserToStimulusSeconds, "
     "-spouts.stimulusToSpoutsInSeconds unless spouts.alwaysIn, 0) in the task file (Settings_Task), each 0 for a task file without its block, plus quietWaitSeconds where the task has quiet waits."),
    ("stimulusOnsetTime", "double", "s",
     "Harp time the workflow commanded the stimulus on, NaN if it never was: the first tick of the 1 ms Harp clock at "
     "or after trialStartTime plus the lead, in every experiment. The physical onset is in the hardware logs."),
    ("stimulusOffTime", "double", "s",
     "Harp time the workflow commanded the stimulus off (OpenRigs.Trials StimulusTiming): the earlier of stimulusOnsetTime "
     "plus stimulus.durationSeconds and outcomeTime plus stimulus.responseToStimulusOffSeconds (unless null), at the first tick at "
     "or after it; known at the outcome, so logged in the row although it may come later. NaN if the stimulus never came "
     "on, or in a task whose stimulus the trial loop does not time. A task that ends it sooner itself says so (its note)."),
    ("outcomeTime", "double", "s",
     "Harp time the workflow knew the outcome, the same time as harpTime, as its own column so that every moment of a "
     "trial is named; the reward, the penalty sound and the timeout are timed from it. On a lick it is the Harp clock's "
     "latest tick when the workflow saw the lick, a few ms after it; each lick's own Harp time is in the lick "
     "detectors' logs."),
    ("rewardOfferedUl", "double", "ul",
     "Microlitres a reward on this trial gives, on every trial whether or not it was rewarded: the size the task drew "
     "for it (the task file's reward size, or a larger reward where the task has one); 0 on a trial that offers none. "
     "rewarded says whether it was given; what was delivered is RewardDelivered in WorkflowData_Events."),
    ("rewarded", "bool", None,
     "True when the board answered the write that opened the valve for the trial's reward (rewardOfferedUl; its "
     "RewardDelivered row in WorkflowData_Events, a few ms after outcomeTime); false on an outcome that earned it when the "
     "board had not answered by the next outcome. A stop before the board answered leaves the trial with no row. Which "
     "outcomes are rewarded is the task's (its note)."),
    ("penaltyTimeoutSeconds", "double", "s",
     "The timeout after this trial when it was penalised, else 0: the trial's penalty.incorrect.timeoutSeconds, or on a "
     "miss its penalty.noResponse.timeoutSeconds. The next interval starts after it (itiStartTime)."),
    ("penaltyFlashBrightness", "double", None,
     "The brightness of the penalty flash after this trial when it was penalised, else 0: the trial's "
     "penalty.incorrect.flash.brightness, or on a miss its penalty.noResponse.flash.brightness, 0 to 1 of the projector's "
     "full white after gamma correction; 0 when no flash played. A flash covers the whole projector field at its kind's "
     "flash.rateHz (white the first half of each cycle) from the outcome for the penalty sound's length (OpenRigs.Vision "
     "PlayPenaltyFlash)."),
    ("penalised", "bool", None,
     "True when the trial was penalised: the timeout (penaltyTimeoutSeconds) and the penalty sound at the trial's "
     "penalty.incorrect.sound.levelDbfs, or on a miss its penalty.noResponse.sound.levelDbfs (none when mute), followed "
     "its outcome. Which trials are penalised, and which are misses, is the task's (its note)."),
]

QUIET_WAIT_COLUMNS: List[Column] = [
    ("quietWaitSeconds", "double", "s",
     "How long the quiet waits (lickQuietSeconds, wheelQuietSeconds and wheelQuietCm; OpenRigs.Core QuietWait) "
     "held this trial's start past the end of its interval less the lead; 0 with the waits off or a quiet mouse."),
]

TRIAL_END_COLUMNS: List[Column] = [
    ("trialEndTime", "double", "s",
     "Harp time the trial ended and its timeout began: its outcomeTime; in a task whose response window always runs in full after a "
     "choice (responseWindowAlwaysFull; OpenRigs.Trials TrialTiming.ResponseWindowAlwaysFull), the response window's end, rewarded "
     "or not (outcomeTime on a miss). The next interval starts at it plus penaltyTimeoutSeconds (itiStartTime). Known at "
     "the outcome, so logged in the row although it may come later."),
]

OPTOGENETICS_COLUMNS: List[Column] = [
    ("laserCondition", "int", None,
     "The Zapit condition ZapitDaq was armed with this trial (the bank's and its conditions file's number, from 1), laser on or off: every trial "
     "of an optogenetics experiment has one, so laser on is laserPowerMw > 0, not laserCondition > 0. 0 without "
     "optogenetics."),
    ("laserPointsMm", "string", "mm",
     "Where Zapit aimed the laser this trial: laserCondition's points in the conditions file (Settings_ZapitConditions; the "
     "bank's stereotaxic_mm), in mm from bregma, each point its AP then its ML separated by a space, points in the file's order separated "
     "by ; (stimLocations01 with ML [1.5, -1.5] and AP [-0.7, -0.7] is -0.7 1.5;-0.7 -1.5). Filled whenever "
     "laserCondition is above 0, laser on or off (at 0 mW the galvos still move there); empty without optogenetics."),
    ("laserPowerMw", "double", "mW",
     "Laser power ZapitDaq armed this trial with: the task's optogenetics.laserPowerMw capped at the laser's maximum and rig.yml's "
     "zapit.ceilingMw (the bank's capped_power_mw), as commanded, not measured. 0 on "
     "laser-off trials, without optogenetics, and on a trial answered before its trigger."),
    ("laserTriggerTime", "double", "s",
     "Harp time the workflow raised the Zapit trigger line (the laser, or at 0 mW the galvos and masking light alone): "
     "the first tick of the 1 ms Harp clock at or after laserToStimulusSeconds before the stimulus was due. NaN without "
     "optogenetics and on a trial that ended before its trigger, so a laser-off trial (a time, laserPowerMw 0) and one "
     "answered first (NaN) differ."),
]

CONDITIONS_COLUMNS: List[Column] = [
    ("trialConditionId", "int", None,
     "The 0-based index of the trial's condition in trialConditions of the task file as run (Settings_Task), which gives its "
     "weight, pairs and condition-level values (set), from which its trialType is derived at Go. A repeat keeps the condition of "
     "the trial it repeats."),
    ("trialCombinationId", "int", None,
     "The trial's row in the experiment's Settings_Combinations (0-based, session-wide): the combination it was drawn from, one "
     "pick of every list (pairs zipped), whose values that differ between combinations or from Settings_Task are the row's "
     "values; the row's trialConditionId and trialType are this trial's. A repeat keeps the combination of the trial it repeats."),
]

STIMULUS_COLUMNS: List[Column] = [
    ("visualInitialContrast", "double", None,
     "Grating contrast as the trial starts, as BonVision's Contrast, 1 full contrast; 0 when no grating was shown."),
    ("visualPhaseRad", "double", "rad",
     "The grating's phase, drawn uniformly from 0 to 2 pi for every trial from the seeded stream (OpenRigs.Trials ConditionedTrial)."),
    ("auditoryInitialLevelDbfs", "double", "dBFS",
     "The sound's level in dBFS RMS as the trial starts, the trial's auditoryStimulus.initialLevelDbfs; NaN when no sound played (mute)."),
    ("auditoryInitialAzimuthDeg", "double", "deg",
     "Azimuth of the speaker that played, rig.yml's nearest to the initial azimuth drawn; NaN when none played "
     "(auditoryInitialLevelDbfs NaN) or when every speaker played (auditoryStimulus.initialAzimuthsDeg all)."),
    ("auditoryInitialElevationDeg", "double", "deg",
     "Elevation of the speaker that played as the trial starts; NaN when none or every speaker played, as auditoryInitialAzimuthDeg."),
]

AUTO_REWARD_COLUMNS: List[Column] = [
    ("autoReward", "bool", None,
     "True on an auto-reward trial (drawn with the task's autoRewardProbability): one rewarded without a correct response, "
     "its outcome auto when the auto-reward answered it with no response of the mouse's own; "
     "how, and so whether its reward came from the auto-reward, is the task's (its note)."),
]

# Each opt-in block by the name a task gives it, with the core column it follows; blocks after the same column follow it in
# this order.
BLOCKS: Dict[str, Tuple[str, List[Column]]] = {
    "quietWait": ("itiSeconds", QUIET_WAIT_COLUMNS),
    "trialEnd": ("outcomeTime", TRIAL_END_COLUMNS),
    "optogenetics": ("penalised", OPTOGENETICS_COLUMNS),
    "conditions": ("penalised", CONDITIONS_COLUMNS),
    "stimulus": ("penalised", STIMULUS_COLUMNS),
    "autoReward": ("penalised", AUTO_REWARD_COLUMNS),
}

TIME_COLUMN = "harpTime"
TOLERANCE_S = 0.002  # Harp-clock waits land within a tick or two of 1 ms
REWARD_ANSWER_S = 0.1  # a RewardDelivered row is the board's answer to the valve's write: 2 to 26 ms after the outcome on a rig


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


OUTCOMES = ("correct", "incorrect", "noResponse", "probe", "auto")  # in the task's camelCase, as its targets and trialType
GO_NO_GO = ("go", "noGo", "noReward")  # a go/no-go task's targets: one spout by design, noReward its probe


def _auto(r: Dict[str, Any]) -> bool:
    """Whether the auto-reward answered a trial row: outcome auto, or in a log from before that outcome, autoReward and
    rewarded with no choice made (choice noResponse) or, with no choice column, correct."""
    if r.get("outcome") == "auto":
        return True
    if not (_true(r.get("autoReward", False)) and _true(r.get("rewarded", False))):
        return False
    return str(r.get("choice") or "noResponse") == "noResponse" if "choice" in r else r.get("outcome") == "correct"


def _responded(r: Dict[str, Any]) -> bool:
    """Whether the mouse responded in a trial row: its choice where the row has one, else an outcome other than noResponse."""
    if "choice" in r:
        return str(r.get("choice") or "noResponse") != "noResponse"
    return str(r.get("outcome") or "noResponse") != "noResponse"


def performance(rows: Iterable[Dict[str, Any]], licks: Optional[Dict[int, bool]] = None) -> Tuple[float, float]:
    """Performance and the response rate of trial rows (text or typed cells), each NaN with no trial to count.

    Performance is correct / (correct + incorrect) over the trials not offered a single spout as a guide (singleSpoutOffered
    False, as where the task does not record it) and every go/no-go trial (target go or noGo; one spout by design), a go
    trial with no lick (noResponse) counted incorrect. The response rate is the share of trials the mouse responded to: in
    a go/no-go task (a target go, noGo or noReward) the go trials, else every trial, probes too, of those the ones responded to:
    where the row has a choice, any choice but noResponse (a withheld noGo is none; an auto-reward trial where the mouse
    chose a side is one); with no choice column, an outcome not noResponse. A trial the auto-reward answered (outcome auto,
    or in an older log autoReward and rewarded with no choice made, or correct with no choice column: a trial the drift
    carried into the reward zone) is responded to when the mouse licked in it, between its stimulus onset and its end
    (licks, by trialNumber), and not when it did not; where its licks are unknown it is left out. A probe (no right
    answer) is left out of Performance; in the response rate it is responded to when the mouse made a choice and not when
    its choice is noResponse, and left out with no choice column (its outcome, probe, does not say)."""
    rows = list(rows)
    licks = licks or {}
    go_no_go = any(str(r.get("target")) in GO_NO_GO for r in rows)
    correct = counted = 0
    for r in rows:
        target, outcome = str(r.get("target")), r["outcome"]
        go_miss = target == "go" and outcome == "noResponse"
        if (not _true(r.get("singleSpoutOffered", False)) or target in ("go", "noGo")) and (outcome in ("correct", "incorrect") or go_miss):
            counted += 1
            correct += outcome == "correct"
    answered = asked = 0
    for r in rows:
        if go_no_go and str(r.get("target")) != "go" or r["outcome"] == "probe" and "choice" not in r:
            continue
        if _auto(r):
            number = _trial_number(r)
            licked = licks.get(number) if number is not None else None
            if licked is None:
                continue  # licks unknown: left out
            answered += bool(licked)
        else:
            answered += _responded(r)
        asked += 1
    return (correct / counted if counted else float("nan"), answered / asked if asked else float("nan"))


def _trial_number(r: Dict[str, Any]) -> Optional[int]:
    try:
        return int(float(str(r.get("trialNumber"))))
    except (TypeError, ValueError):  # no number there: the row has no trial number
        return None


@dataclass
class Summary:
    folder: str
    run_outcome: str  # the manifest's run outcome (completed, stopped, ...), "not final" when it never closed; or unknown
    trials: int
    correct: int
    performance: float  # performance()'s first: correct / (correct + incorrect), two-spout and go/no-go trials; NaN with none
    responded: float = float("nan")  # performance()'s second: the share responded to; NaN with none
    response_seconds: List[float] = field(default_factory=list)  # each trial's outcome less its stimulus onset
    interval_seconds: List[float] = field(default_factory=list)  # each trial's end (trialEndTime, else outcomeTime) to the next stimulus onset
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


def _end(row: Dict[str, str]) -> float:
    """When a trial ended and its timeout began: trialEndTime, or outcomeTime in a log without the trialEnd block."""
    return _f(row, "trialEndTime") if "trialEndTime" in row else _f(row, "outcomeTime")


def _true(text: Any) -> bool:
    return str(text).strip().lower() == "true"


# The columns summarise reads: of a trial row, and of an Events row.
TRIAL_NEEDS = ("trialNumber", "outcome", "itiStartTime", "itiSeconds", "trialStartTime", "stimulusOnsetTime", "outcomeTime", "rewardOfferedUl", "rewarded", "penaltyTimeoutSeconds", "penalised")
EVENT_NEEDS = (TIME_COLUMN, "Event", "Value")
# A trial row's numbers, and those of them that may be NaN, by their descriptions above; any other NaN, an infinity, an
# empty cell or text is a problem and the row's checks are skipped.
TRIAL_NUMBERS = ("trialNumber", "itiStartTime", "itiSeconds", "trialStartTime", "stimulusOnsetTime", "outcomeTime",
                 "rewardOfferedUl", "penaltyTimeoutSeconds", "penaltyFlashBrightness", "quietWaitSeconds", "laserTriggerTime",
                 "trialEndTime")
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
            except ValueError:  # requested= without a number: as without one
                return None
    return None


INCORRECT_TIMEOUT_PATH = "penalty.incorrect.timeoutSeconds"  # a penalised choice's timeout
MISS_TIMEOUT_PATH = "penalty.noResponse.timeoutSeconds"  # a penalised miss's; penalty.noResponse null: misses never penalised
_MISSING = object()


def _at(values: Any, path: str) -> Any:
    """A dotted path in nested task values, _MISSING where a key is not there, None under a null block."""
    for key in path.split("."):
        if values is None:
            return None
        if not isinstance(values, dict) or key not in values:
            return _MISSING
        values = values[key]
    return values


@dataclass
class _Task:
    params: Dict[str, Any]  # the task file's taskParameters
    combinations: Optional[Dict[int, Dict[str, Any]]]  # Settings_Combinations' values by trialCombinationId; None without it

    def value(self, row: Dict[str, str], path: str, default: Any) -> Any:
        """A trial's value at a dotted path: its combination's where it sets it (or a block above it), else the task
        file's, else default. Raises ValueError for a trialCombinationId the table does not have."""
        if self.combinations is not None and trial_log.ID in row:
            combination = self.combinations.get(int(float(row[trial_log.ID])))
            if combination is None:
                raise ValueError(f"{trial_log.ID} {row[trial_log.ID]} is not a row of {trial_log.COMBINATIONS_FILE[1]}")
            parts = path.split(".")
            for n in range(len(parts), 0, -1):
                key = ".".join(parts[:n])
                if key in combination:
                    found = _at({"v": combination[key]}, ".".join(["v"] + parts[n:]))
                    return default if found is _MISSING else found
        found = _at(self.params, path)
        return default if found is _MISSING else found

    def laser_to_stimulus(self, row: Dict[str, str]) -> float:
        return float(self.value(row, "optogenetics.laserToStimulusSeconds", 0.0) or 0.0)

    def lead(self, row: Dict[str, str]) -> float:
        """max(laserToStimulusSeconds, -spouts.stimulusToSpoutsInSeconds unless spouts.alwaysIn, 0)."""
        spouts = 0.0 if self.value(row, "spouts.alwaysIn", False) else -float(self.value(row, "spouts.stimulusToSpoutsInSeconds", 0.0) or 0.0)
        return max(self.laser_to_stimulus(row), spouts, 0.0)

    def timeout(self, row: Dict[str, str]) -> float:
        """A penalised trial's timeout: penalty.incorrect.timeoutSeconds, or on a miss (no choice where the row has one,
        else outcome noResponse) penalty.noResponse.timeoutSeconds, its combination's where it sets it. Raises ValueError
        for a penalised miss whose penalty.noResponse is null."""
        if _responded(row):
            return float(self.value(row, INCORRECT_TIMEOUT_PATH, PENALTY_TIMEOUT_SECONDS))
        miss = self.value(row, MISS_TIMEOUT_PATH, PENALTY_TIMEOUT_SECONDS)
        if miss is None:
            raise ValueError("a penalised miss, but penalty.noResponse is null")
        return float(miss)

    def flash_brightness(self, row: Dict[str, str]) -> float:
        """A penalised trial's flash brightness: its kind's flash.brightness (as timeout() picks the kind), 0 by default."""
        kind = "penalty.incorrect" if _responded(row) else "penalty.noResponse"
        return float(self.value(row, kind + ".flash.brightness", 0.0) or 0.0)


def _task(folder: Path) -> Optional[_Task]:
    """The experiment's task file values the checks need (Settings/Settings_Task_<time>.json), and its combinations'
    (Settings/Settings_Combinations_<time>.json) where it has one, or None."""
    for path in _chunks(folder, TASK_FILES):
        try:
            params = json.loads(path.read_text(encoding="utf-8-sig"))["taskParameters"]
            if not isinstance(params, dict):
                return None
            tables = _chunks(folder, trial_log.COMBINATIONS_FILE)
            combinations = None
            if tables:
                rows = json.loads(tables[0].read_text(encoding="utf-8-sig"))["rows"]
                combinations = {int(r[trial_log.ID]): r["values"] for r in rows}
            return _Task(params, combinations)
        except (OSError, ValueError, KeyError, TypeError):  # unreadable: None, as the docstring says; the caller notes it
            return None
    return None


def summarise(folder: Path, videos: bool = False) -> Summary:
    """One experiment's summary and failed checks, from its files alone; with videos, each camera video against its CSV."""
    folder = Path(folder)
    s = _summarise(folder)
    if not folder.is_dir():  # a mistyped folder reads as an empty run: said, so main exits 1
        s.problems.append(f"{folder} is not a folder")
        return s
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
        run_outcome = (run["outcome"] or "unknown") + (" (not final: the workflow never finished closing)" if run.get("final") is False else "")
    except (OSError, ValueError, KeyError, TypeError):
        run_outcome = "unknown"
    missing = [f"WorkflowData_Trials has no {c} column" for c in TRIAL_NEEDS if trials and c not in trials[0]]
    missing += [f"WorkflowData_Events has no {c} column" for c in EVENT_NEEDS if events and c not in events[0]]
    if missing:
        return Summary(folder.name, run_outcome, len(trials), 0, float("nan"), problems=missing)
    correct = sum(r["outcome"] == "correct" for r in trials)
    # It reads no lick data (licks are in the lick detectors' logs), so an auto trial's licks are unknown: left out.
    s = Summary(folder.name, run_outcome, len(trials), correct, *performance(trials))
    task = _task(folder)
    if trials and task is None:
        s.problems.append("no readable Settings/Settings_Task_*.json: Go, the laser trigger and the timeout not checked")
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
            try:
                lead, laser_lead = task.lead(row), task.laser_to_stimulus(row)
            except (ValueError, TypeError) as e:
                s.problems.append(f"trial {number}: {e}, so its lead is not checked")
                lead = laser_lead = float("nan")
            go = _f(row, "itiStartTime") + planned - lead
            if not math.isnan(lead) and abs(_f(row, "trialStartTime") - go) > TOLERANCE_S:
                s.problems.append(f"trial {number}: trial start (trialStartTime) {_f(row, 'trialStartTime') - go:+.4f} s from its "
                                  f"interval's end less the lead ({lead:g} s) plus quietWaitSeconds")
            trigger = _f(row, "laserTriggerTime") if "laserTriggerTime" in row else float("nan")
            due = _f(row, "itiStartTime") + planned - laser_lead
            if not math.isnan(trigger) and not math.isnan(laser_lead) and abs(trigger - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: laser trigger {trigger - due:+.4f} s from laserToStimulusSeconds "
                                  f"({laser_lead:g} s) before the stimulus was due")
            penalised, timeout = _true(row["penalised"]), _f(row, "penaltyTimeoutSeconds")
            try:
                want = task.timeout(row) if penalised else 0.0
            except (ValueError, TypeError) as e:
                s.problems.append(f"trial {number}: {e}, so its timeout is not checked")
                want = timeout
            if abs(timeout - want) > 1e-9:
                s.problems.append(f"trial {number}: penaltyTimeoutSeconds {timeout:g} on a trial "
                                  f"{'penalised' if penalised else 'not penalised'}, not {want:g}")
            if "penaltyFlashBrightness" in row:
                flash = _f(row, "penaltyFlashBrightness")
                try:
                    want = task.flash_brightness(row) if penalised else 0.0
                except (ValueError, TypeError) as e:
                    s.problems.append(f"trial {number}: {e}, so its flash is not checked")
                    want = flash
                if abs(flash - want) > 1e-9:
                    s.problems.append(f"trial {number}: penaltyFlashBrightness {flash:g} on a trial "
                                      f"{'penalised' if penalised else 'not penalised'}, not {want:g}")
        if "trialEndTime" in row and _f(row, "trialEndTime") < end - TOLERANCE_S:
            s.problems.append(f"trial {number}: trialEndTime {_f(row, 'trialEndTime') - end:+.4f} s from its outcome, before it")
        if last is not None:
            due = _end(last) + _f(last, "penaltyTimeoutSeconds")
            if abs(_f(row, "itiStartTime") - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: interval started {_f(row, 'itiStartTime') - due:+.4f} s from the last "
                                  + ("trial's end (trialEndTime)" if "trialEndTime" in last else "outcome") + " plus its timeout")
            if shown:
                s.interval_seconds.append(onset - _end(last))
        last = row
        at_outcome = [r for t, r in reward_times if end - TOLERANCE_S <= t <= end + REWARD_ANSWER_S]
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


# WorkflowData_StimulusState's stimulus columns (instantaneous values, never renamed: not the trial log's
# auditoryInitialLevelDbfs and visualInitialContrast), each with when its value says the stimulus is on.
STIMULUS_ON_COLUMNS = {
    "auditoryLevelDbfs": lambda v: not math.isnan(float(v)),
    "visualContrast": lambda v: float(v) > 0,
}


def _unfinished(s: Summary, folder: Path, trials: List[Dict[str, str]], last: Optional[Dict[str, str]]) -> None:
    """The trial after the last finished one, when StimulusState has rows for it: the run stopped in it. Its first
    row with a stimulus on (auditoryLevelDbfs not NaN or visualContrast above 0, whichever columns the log has) is the stimulus the last outcome's interval ran to."""
    rows = _rows(folder, STIMULUS_FILES)
    if not rows or not trials:
        return
    onsets = [c for c in STIMULUS_ON_COLUMNS if c in rows[0]]
    if any(c not in rows[0] for c in ("harpTime", "trialNumber")) or not onsets:
        s.problems.append("WorkflowData_StimulusState lacks harpTime, trialNumber, or a stimulus column "
                          "(auditoryLevelDbfs, visualContrast): the unfinished trial not looked for")
        return
    try:
        cut = int(_f(trials[-1], "trialNumber")) + 1
    except (TypeError, ValueError):  # the last trial has no number: no unfinished trial to look for
        return
    mine = []
    for r in rows:
        try:
            if int(_f(r, "trialNumber")) == cut:
                mine.append(r)
        except (TypeError, ValueError):  # a row without a trial number belongs to no trial
            continue
    if not mine:
        return
    s.unfinished = cut
    if last is None:
        return
    for r in mine:
        try:
            if any(STIMULUS_ON_COLUMNS[c](r[c]) for c in onsets):
                s.interval_seconds.append(_f(r, TIME_COLUMN) - _end(last))
                return
        except (TypeError, ValueError):  # a stimulus or time that is not a number: passed over
            continue


def describe(s: Summary) -> str:
    """The summary as lines of text, each failed check a PROBLEM line."""
    def spread(values: List[float]) -> str:
        return f"median {statistics.median(values):.2f} s (range {min(values):.2f} to {max(values):.2f})" if values else "none"

    def percent(x: float) -> str:
        return "none" if math.isnan(x) else f"{x:.0%}"

    cut = f", and trial {s.unfinished} unfinished at the stop" if s.unfinished is not None else ""
    lines = [f"{s.folder}: {s.run_outcome}, {s.trials} trials, {s.correct} correct{cut}",
             f"  performance {percent(s.performance)} (correct / (correct + incorrect), two-spout and go/no-go trials),"
             f" responded {percent(s.responded)}",
             f"  response (outcome less stimulus onset): {spread(s.response_seconds)}",
             f"  trial end to next stimulus:{spread(s.interval_seconds)}",
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
