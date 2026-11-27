"""Play Condition: a condition's real trial before Go, from the loaded bank, as the task's own trials play it.

The sequence is the user's (27 Nov 2026): no new ZapitDaq command. The condition is armed with ZapitDaq's own Arm (the
workflow's Zapit-Main.Play, lib-open-rigs ZapitPlay, trial 0), the ZapitTrigger line is raised to start it (Zapit-Main.Trigger
on, as a trial's trigger raises it; the masking light with it) and cleared once the trial with its ramp has played, then
every output zeroed (Zapit-Main.LaserOff). A stop (Laser Off, Stop, Go) clears the line and zeroes the outputs at once.

``plan_play`` chooses what plays: the condition at the bank's laser-on power (the task's ``laserPowerMw``, held under
``ceilingMw`` when the bank was built, and refused here above it), for the task's laser duration, else
``PLAY_DEFAULT_S``, never longer than ZapitDaq's trial buffer takes with the ramp (as lib-open-rigs ZapitBank.Cycles judges
it). ``ConditionPlay`` runs the sequence on its own thread with the device actions given to it, so a caller (the rig app's
agent) only relays the request and shows the state. Its laser is never "off" while it may be on: from the Arm until the clear
and the zero are delivered it reads "play", and a clear or zero not delivered reads "unknown" until ``retry`` delivers one.
Free of hardware, Qt and sockets: the actions are the caller's.
"""
from __future__ import annotations

import math
import threading
import time
from dataclasses import dataclass
from typing import Callable

SETUP_TRIAL = 0  # lib-open-rigs ZapitPlay.SetupTrial: a task's trials count from 1
PLAY_DEFAULT_S = 1.0  # a play when the task gives no laser duration: long enough to see, short of any trial buffer
ARM_TIMEOUT_S = 3.0  # ZapitDaq arms a trial in well under this (ZapitDaqTrial.ArmSeconds); none by then is a refusal
END_MARGIN_S = 0.1  # the line is cleared this long after the trial's own end (the DAQ ends it, laser and LED at 0 V)
POWER_MATCH = 1e-4  # relative: a power asked matches a bank variant's within this


class PlayRefused(ValueError):
    """A play that cannot be made, and why (Title Case is the caller's)."""


@dataclass(frozen=True)
class PlayPlan:
    """What one play plays: the condition (1-based), the bank variant's power (sent exactly as ZapitDaq matches it), the
    laser's seconds before the ramp (whole scan cycles), the trial's seconds with its ramp, and a note on how it was
    chosen ("" when as asked)."""
    condition: int
    power_mw: float
    seconds: float
    trial_seconds: float
    note: str = ""

    def request(self) -> str:
        """The workflow's Zapit-Main.Play args: condition,powerMw,seconds (repr: ZapitDaq matches the power exactly)."""
        return f"{self.condition},{self.power_mw!r},{self.seconds!r}"


def _rows(cycles: int, n: int, variant: dict) -> int:
    """A trial's rows with its ramp tail, as ZapitBank.TrialRows counts them (no ramp: a one-row tail at 0 V that
    replaces the body's last row)."""
    tail_cycles = int(variant.get("tail_cycles") or 0)
    replaces = int(variant.get("tail_replaces", 0 if tail_cycles else 1))
    return cycles * n - replaces + (tail_cycles * n if tail_cycles else 1)


def plan_play(index: dict, condition: int, ceiling_mw: float, buffer_seconds: float | None,
              task_seconds: float | None = None, power_mw: float | None = None) -> PlayPlan:
    """The play of `condition` (1-based) from the bank's `index` (open_rigs.zapit.bank's JSON): at `power_mw` where the
    bank has it, else its first laser-on power (the task's); for `task_seconds` (the task's laser duration), else
    PLAY_DEFAULT_S, shortened to the most whole scan cycles that fit `buffer_seconds` (ZapitDaq's trial buffer) with the
    ramp. Raises PlayRefused with the laser disabled (ceilingMw 0), no buffer known, no such condition, no laser-on
    variant, a power above ceilingMw, or no laser that fits."""
    if not ceiling_mw > 0:
        raise PlayRefused("the laser is disabled (ceilingMw 0), so there is nothing to play")
    if buffer_seconds is None or not buffer_seconds > 0:
        raise PlayRefused("ZapitDaq's trial buffer is not known, so no play can be sized")
    conditions = index["conditions"]
    if not 1 <= condition <= len(conditions):
        raise PlayRefused(f"the waveforms have no condition {condition}; they have {len(conditions)}")
    on = [v for v in conditions[condition - 1]["variants"] if v.get("laser_on")]
    if not on:
        raise PlayRefused(f"condition {condition} has no laser-on waveform")
    variant = on[0]
    if power_mw is not None:
        variant = next((v for v in on if abs(float(v["power_mw"]) - power_mw) <= POWER_MATCH * max(abs(power_mw), 1e-3)),
                       None)
        if variant is None:
            held = ", ".join(f"{float(v['power_mw']):g}" for v in on)
            raise PlayRefused(f"condition {condition} has no waveform at {power_mw:g} mW (it has {held} mW)")
    power = float(variant["power_mw"])
    delivered = float(variant.get("capped_power_mw", power))
    if power > ceiling_mw * (1 + POWER_MATCH) or delivered > ceiling_mw * (1 + POWER_MATCH):
        raise PlayRefused(f"condition {condition}'s {power:g} mW is above this rig's ceilingMw ({ceiling_mw:g} mW)")
    rate = float(index["sample_rate"])
    n = int(conditions[condition - 1]["scan_cycle"]["length"])
    cap_rows = math.floor(buffer_seconds * rate + 1e-6)
    asked = float(task_seconds) if task_seconds is not None and task_seconds > 0 else PLAY_DEFAULT_S
    note = "" if task_seconds is not None and task_seconds > 0 else f"the task gives no laser duration, so {asked:g} s"
    cycles = math.ceil(asked * rate / n - 1e-9)
    if _rows(cycles, n, variant) > cap_rows:
        most = max(c for c in range(0, cycles + 1) if c == 0 or _rows(c, n, variant) <= cap_rows)
        if most < 1:
            raise PlayRefused(f"no laser of condition {condition} fits ZapitDaq's {buffer_seconds:g} s trial buffer "
                              "with its ramp")
        cycles = most
        note = f"{asked:g} s with its ramp is longer than ZapitDaq's {buffer_seconds:g} s trial buffer, so {cycles * n / rate:g} s"
    return PlayPlan(condition, power, cycles * n / rate, _rows(cycles, n, variant) / rate, note)


def play_problem(*, went: bool, laser_disabled: bool, bank_loaded: bool, sample_accepted: bool, busy: bool = False,
                 playing: bool = False, unknown: bool = False) -> str:
    """Why Play Condition cannot be pressed now, "" when it can: before Go only, with the laser enabled, once the
    waveforms are loaded and the sample accepted, while no setup step runs, no play is on and none was left unknown."""
    if went:
        return "Play Condition is before Go only"
    if laser_disabled:
        return "the laser is disabled (ceilingMw 0)"
    if not sample_accepted:
        return "accept the sample first"
    if not bank_loaded:
        return "the waveforms are not loaded yet"
    if unknown:
        return "the last play's Laser Off was not delivered: press Laser Off"
    if playing:
        return "a condition is playing"
    if busy:
        return "a setup step is running"
    return ""


class ConditionPlay:
    """One play at a time, on its own thread. The actions, each raising when not delivered:

    - ``arm(request)``: send Zapit-Main.Play with ``PlayPlan.request()``;
    - ``line(on)``: send Zapit-Main.Trigger, on or off (the ZapitTrigger line);
    - ``zero()``: send Zapit-Main.LaserOff (every output at 0 V; the workflow clears the line with it);
    - ``stage()``: the workflow's latest Zapit-Main.Play metric ("Armed 2", "Refused <why>", ...).

    ``state``: "off"; "arming" (the Arm sent: the trial may be armed, the line not yet raised); "playing" (the line
    raised: the laser plays); "unknown" (a clear or zero not delivered: the laser may be on). ``laser``: "play" from the
    Arm to the delivered clear and zero, "unknown" after one not delivered, else "off". The line is never raised once a
    stop has begun: the raise and the stop are taken one at a time."""

    def __init__(self, arm: Callable[[str], None], line: Callable[[bool], None], zero: Callable[[], None],
                 stage: Callable[[], str], clock: Callable[[], float] = time.monotonic,
                 arm_timeout_s: float = ARM_TIMEOUT_S, end_margin_s: float = END_MARGIN_S, poll_s: float = 0.02):
        self._arm, self._line, self._zero, self._stage = arm, line, zero, stage
        self.clock, self.arm_timeout_s, self.end_margin_s, self.poll_s = clock, arm_timeout_s, end_margin_s, poll_s
        self._lock = threading.Lock()  # the line's raise and a stop's clear, one at a time
        self._stopped = threading.Event()
        self._thread: threading.Thread | None = None
        self.state = "off"
        self.plan: PlayPlan | None = None
        self.started_at: float | None = None  # when the line went up (clock)
        self.why = ""  # the last play's end or refusal, said once by the caller

    # ---- what a caller shows -------------------------------------------------------------
    @property
    def laser(self) -> str:
        return {"arming": "play", "playing": "play", "unknown": "unknown"}.get(self.state, "off")

    @property
    def active(self) -> bool:
        return self.state in ("arming", "playing")

    def describe(self) -> dict:
        """{state, laser, condition, power_mw, seconds, left_s (until the trial's own end while playing), note, why}."""
        plan, started = self.plan, self.started_at
        left = None
        if self.state == "playing" and plan is not None and started is not None:
            left = max(0.0, plan.trial_seconds - (self.clock() - started))
        return {"state": self.state, "laser": self.laser, "condition": plan.condition if plan else None,
                "power_mw": plan.power_mw if plan else None, "seconds": plan.seconds if plan else None,
                "left_s": left, "note": plan.note if plan else "", "why": self.why}

    # ---- the sequence --------------------------------------------------------------------
    def start(self, plan: PlayPlan) -> str:
        """Starts `plan` on its own thread: "" when started, else why not (one on, or one left unknown)."""
        with self._lock:
            if self.state == "unknown":
                return "the last play's Laser Off was not delivered: press Laser Off"
            if self.active:
                return f"condition {self.plan.condition if self.plan else '?'} is playing"
            self._stopped = threading.Event()
            self.plan, self.started_at, self.why, self.state = plan, None, "", "arming"
            stopped = self._stopped
        self._thread = threading.Thread(target=self._run, args=(plan, stopped), daemon=True, name="zapit play")
        self._thread.start()
        return ""

    def _run(self, plan: PlayPlan, stopped: threading.Event):
        try:
            before = self._stage()
            try:
                self._arm(plan.request())
            except Exception as error:  # noqa: BLE001 - it may have been taken: cleared and zeroed all the same
                self.stop(f"the workflow did not take the play: {error}", stopped)
                return
            why = self._await_armed(plan, before, stopped)
            if why is not None:  # refused, not armed in time, or stopped already ("")
                if why:
                    self.stop(why, stopped)
                return
            with self._lock:  # never raised once a stop has begun
                if stopped.is_set():
                    return
                try:
                    self._line(True)
                    self.started_at, self.state = self.clock(), "playing"
                except Exception as error:  # noqa: BLE001 - the line may be up: cleared below
                    raised = error
                else:
                    raised = None
            if raised is not None:
                self.stop(f"the ZapitTrigger line was not raised: {raised}", stopped)
                return
            if not stopped.wait(plan.trial_seconds + self.end_margin_s):
                self.stop("", stopped)  # its own end: the line cleared, the outputs zeroed
        except Exception as error:  # noqa: BLE001 - never a thread that dies with the line up
            self.stop(f"the play failed: {error}", stopped)

    def _await_armed(self, plan: PlayPlan, before: str, stopped: threading.Event) -> str | None:
        """None once the workflow says Armed; else why the play ends ("" for a stop already made)."""
        deadline = self.clock() + self.arm_timeout_s
        while not stopped.is_set():
            now = str(self._stage() or "")
            stage, _, detail = now.partition(" ")
            if now != before and stage == "Armed" and detail.strip() == str(plan.condition):
                return None
            if now != before and stage == "Refused":
                return f"the workflow refused the play: {detail}"
            if self.clock() > deadline:
                return f"condition {plan.condition} was not armed within {self.arm_timeout_s:g} s"
            stopped.wait(self.poll_s)
        return ""

    def stop(self, why: str = "stopped", stopped: threading.Event | None = None) -> str:
        """Ends the play: the line cleared and every output zeroed, now (Laser Off, Stop, Go, the play's own end).
        "" when both were delivered (the laser reads "off"), else what was not (it reads "unknown"; `retry` sends them
        again). A play that is off already sends nothing."""
        with self._lock:
            if stopped is not None and stopped is not self._stopped:
                return ""  # an earlier play's thread, ended already
            self._stopped.set()
            if self.state == "off":
                return ""
            failed = []
            for name, action in (("the ZapitTrigger line's clear", lambda: self._line(False)), ("Laser Off", self._zero)):
                try:
                    action()
                except Exception as error:  # noqa: BLE001 - said, and the laser reads "unknown"
                    failed.append(f"{name} was not sent ({error})")
            self.state = "unknown" if failed else "off"
            self.why = "; ".join(failed) if failed else why
            return self.why if failed else ""

    def retry(self) -> str:
        """A clear and zero not delivered, sent again: "" once both are (the laser reads "off")."""
        with self._lock:
            if self.state != "unknown":
                return ""
            self.state = "playing"  # the laser may be on until this stop delivers
        return self.stop("after a Laser Off that was not delivered")

    def join(self, timeout: float | None = None):
        if self._thread is not None:
            self._thread.join(timeout)
