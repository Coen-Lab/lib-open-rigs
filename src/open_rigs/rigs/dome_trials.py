"""The dome tasks' trial log: the columns every dome task shares, and one experiment summarised from its files.

Every dome task's schemas/trial-columns.yml starts with SHARED_COLUMNS, in that order, with these types, units and
descriptions word for word (each task's own tests hold it to them with shared_block_problems), so one analysis reads
every dome task. trialType, outcome, rewardSizeUl and punishSound are in the block by name, type and place, each
described by its task. A task's own columns follow the block.

summarise reads one experiment's BonsaiData/BonsaiData_Trial_*.csv, BonsaiData/BonsaiData_Async_*.csv (each
RewardDelivered row, Value the microlitres delivered) and manifest.json (how the run ended), and checks the rows
against each other:

- trial numbers run 1, 2, 3 ... with none missing;
- each stimulus came itiSeconds plus quietWaitSeconds after its interval started (itiStartTime), within 2 ms;
- each interval after the first started at the last trial's outcome plus its punishTimeoutSeconds, within 2 ms;
- each rewarded trial (rewardSizeUl above 0, and Correct or autoRewarded) has a RewardDelivered row at its outcome.

A file without a column the checks read (harpTime, outcomeTime and the rest of the shared block) is one problem,
not checked further. `summarise-experiment <experiment folder> ...` prints each summary and each
failed check, exiting 1 when one fails.
"""

from __future__ import annotations

import argparse
import csv
import json
import statistics
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

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
     "the time to drink included. The stage's quiet waits add quietWaitSeconds, so the stimulus was commanded at "
     "itiStartTime + itiSeconds + quietWaitSeconds."),
    ("quietWaitSeconds", "double", "s",
     "How long the stage's quiet waits (lickQuietSeconds, wheelQuietSeconds and wheelQuietCm; OpenRigs.Core QuietWait) "
     "held this trial's Go past the end of its interval less the lead; 0 with the waits off or a quiet mouse."),
    ("trialStartTime", "double", "s",
     "Harp time the trial started (Go): the end of its interval less the lead, max(laserToStimulusSeconds, 0) in the "
     "task file (in the manifest), or later by quietWaitSeconds."),
    ("stimulusOnsetTime", "double", "s",
     "Harp time the workflow commanded the stimulus on, NaN if it never was: trialStartTime plus the lead in every "
     "experiment; with optogenetics the laser trigger was laserToStimulusSeconds before it. The physical onset is in "
     "the hardware logs."),
    ("outcomeTime", "double", "s",
     "Harp time the outcome was known, the same time as harpTime, as its own column so that every moment of a trial "
     "is named."),
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


@dataclass
class Summary:
    folder: str
    outcome: str  # the manifest's run outcome (completed, stopped, ...), "not final" when it never closed; or unknown
    trials: int
    correct: int
    performance: float  # correct / trials; NaN with no trials
    response_seconds: List[float] = field(default_factory=list)  # each trial's outcome less its stimulus onset
    interval_seconds: List[float] = field(default_factory=list)  # each outcome to the next stimulus onset
    requested_ul: float = 0.0  # the task's rewards: rewardSizeUl of each rewarded trial
    delivered_ul: float = 0.0  # every RewardDelivered row's Value (manual rewards included)
    problems: List[str] = field(default_factory=list)


def _rows(folder: Path, pattern: str) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    for log in sorted(folder.glob(pattern)):
        with open(log, newline="", encoding="utf-8-sig") as f:
            for row in csv.DictReader(f):
                rows.append(row)
    return rows


def _f(row: Dict[str, str], column: str) -> float:
    return float(row[column])


# The columns summarise reads: of a trial row, and of an async row.
TRIAL_NEEDS = ("trialNumber", "outcome", "itiStartTime", "itiSeconds", "quietWaitSeconds", "stimulusOnsetTime",
               "outcomeTime", "rewardSizeUl", "punishTimeoutSeconds")
ASYNC_NEEDS = (TIME_COLUMN, "Event", "Value")


def _rewarded(row: Dict[str, str]) -> bool:
    return _f(row, "rewardSizeUl") > 0 and (row["outcome"] == "Correct" or str(row.get("autoRewarded", "")).lower() == "true")


def summarise(folder: Path) -> Summary:
    """One experiment's summary and failed checks, from its files alone."""
    folder = Path(folder)
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
    s = Summary(folder.name, outcome, len(trials), correct, correct / len(trials) if trials else float("nan"))
    rewards = [e for e in events if e["Event"] == "RewardDelivered"]
    s.delivered_ul = sum(float(e["Value"] or 0) for e in rewards)
    reward_times = [_f(e, TIME_COLUMN) for e in rewards]
    for n, row in enumerate(trials):
        number = int(row["trialNumber"])
        if number != n + 1:
            s.problems.append(f"row {n + 1} is trial {number}: trial numbers should run 1, 2, 3 ...")
        onset, end = _f(row, "stimulusOnsetTime"), _f(row, "outcomeTime")
        s.response_seconds.append(end - onset)
        planned = _f(row, "itiSeconds") + _f(row, "quietWaitSeconds")
        if abs(onset - _f(row, "itiStartTime") - planned) > TOLERANCE_S:
            s.problems.append(f"trial {number}: stimulus {onset - _f(row, 'itiStartTime'):.4f} s after its interval started, "
                              f"not itiSeconds + quietWaitSeconds = {planned:.4f} s")
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
                s.problems.append(f"trial {number}: rewarded with rewardSizeUl {row['rewardSizeUl']} but no RewardDelivered at its outcome")
    return s


def describe(s: Summary) -> str:
    """The summary as lines of text, each failed check a PROBLEM line."""
    def spread(values: List[float]) -> str:
        return f"median {statistics.median(values):.2f} s (range {min(values):.2f} to {max(values):.2f})" if values else "none"

    lines = [f"{s.folder}: {s.outcome}, {s.trials} trials, {s.correct} correct"
             + (f" ({s.performance:.0%})" if s.trials else ""),
             f"  response (outcome less stimulus onset): {spread(s.response_seconds)}",
             f"  outcome to next stimulus: {spread(s.interval_seconds)}",
             f"  water: {s.requested_ul:g} ul asked by the task, {s.delivered_ul:g} ul delivered (RewardDelivered)"]
    lines += [f"  PROBLEM {p}" for p in s.problems]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="One dome experiment summarised from its files, with its consistency checks.")
    parser.add_argument("folders", nargs="+", type=Path, help="experiment folders (ses-...)")
    args = parser.parse_args()
    failed = False
    for folder in args.folders:
        s = summarise(folder)
        print(describe(s))
        failed |= bool(s.problems)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
