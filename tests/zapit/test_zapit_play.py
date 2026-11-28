"""Play Condition's sequence (open_rigs.zapit.play): what plays, and Arm, line up, timed clear, and the stops."""
import json
import math
import threading
import time
from pathlib import Path

import pytest

from open_rigs.zapit.play import (PLAY_DEFAULT_SECONDS, ConditionPlay, PlayPlan, PlayRefused, most_cycles, trial_rows, play_problem,
                                  plan_play)

RATE = 1000.0


def index(tail_cycles=2, powers=(2.0, 8.0), n=25):
    """A bank index as open_rigs.zapit.bank writes it: two conditions of a 25-row (40 Hz) scan cycle at 1 kHz."""
    def variants():
        return [{"laser_on": False, "power_mw": 0.0, "capped_power_mw": 0.0, "tail_cycles": tail_cycles, "tail_replaces": 0}] + [
            {"laser_on": True, "power_mw": p, "capped_power_mw": p, "tail_cycles": tail_cycles, "tail_replaces": 0} for p in powers]
    return {"sample_rate": RATE, "conditions": [{"scan_cycle": {"length": n}, "variants": variants()} for _ in range(2)]}


def test_a_play_is_the_condition_at_the_banks_power_for_the_tasks_laser():
    plan = plan_play(index(), 2, ceiling_mw=30, buffer_seconds=6, task_seconds=1.5)
    assert (plan.condition, plan.power_mw, plan.seconds, plan.note) == (2, 2.0, 1.5, "")
    assert plan.trial_seconds == pytest.approx(1.55)  # 60 cycles and the 2-cycle ramp
    assert plan.request() == "2,2.0,1.5"
    assert plan_play(index(), 1, 30, 6, 1.5, power_mw=8.0).power_mw == 8.0
    plan = plan_play(index(), 1, 30, 6, None)
    assert plan.seconds == PLAY_DEFAULT_SECONDS and "no laser duration" in plan.note
    assert plan_play(index(), 1, 30, 6, 0.01).seconds == pytest.approx(0.025)  # whole cycles, as ZapitBank.Cycles


def test_a_play_longer_than_the_buffer_is_shortened_to_whole_cycles_that_fit():
    plan = plan_play(index(), 1, 30, buffer_seconds=1.0, task_seconds=3)
    assert plan.trial_seconds <= 1.0 and plan.seconds == pytest.approx(0.95)
    assert "longer than the task's 1 s Zapit trial buffer" in plan.note
    with pytest.raises(PlayRefused, match="no laser of condition 1 fits"):
        plan_play(index(tail_cycles=4), 1, 30, buffer_seconds=0.1, task_seconds=1)


@pytest.mark.parametrize("args, words", [
    ((1, 0, 6), "laser disabled"),
    ((1, 30, None), "Zapit trial buffer is not known"),
    ((3, 30, 6), "no condition 3"),
    ((1, 5, 6), "above this rig's ceilingMw"),
])
def test_a_play_the_rig_or_bank_cannot_make_is_refused(args, words):
    with pytest.raises(PlayRefused, match=words):
        plan_play(index(powers=(8.0,)), *args, task_seconds=1)


def test_the_ceiling_is_exact_but_a_power_asked_matches_a_variant_within_a_tolerance():
    # ZapitPlay refuses power > CeilingMw exactly, so a plan the agent accepts is never refused by the workflow
    assert plan_play(index(), 1, 8.0, 6, 1, power_mw=8.0).power_mw == 8.0  # at the ceiling
    with pytest.raises(PlayRefused, match="above this rig's ceilingMw"):
        plan_play(index(), 1, 8.0 * (1 - 1e-6), 6, 1, power_mw=8.0)  # a ceiling a hair below the power: refused, no slack
    assert plan_play(index(), 1, 30, 6, 1, power_mw=8.0 * (1 + 1e-6)).power_mw == 8.0  # asked within the tolerance
    capped = index()
    capped["conditions"][0]["variants"][1]["capped_power_mw"] = 2.0 + 1e-6
    with pytest.raises(PlayRefused, match="above this rig's ceilingMw"):
        plan_play(capped, 1, 2.0, 6, 1)  # the delivered power is held to the ceiling too


def test_a_power_the_bank_lacks_is_refused():
    with pytest.raises(PlayRefused, match="no waveform at 3 mW"):
        plan_play(index(), 1, 30, 6, 1, power_mw=3.0)


def test_play_condition_is_offered_only_before_go_once_the_waveforms_load_and_the_sample_is_accepted():
    ready = dict(went=False, laser_disabled=False, bank_loaded=True, sample_accepted=True)
    assert play_problem(**ready) == ""
    assert "before Go" in play_problem(**{**ready, "went": True})
    assert "disabled" in play_problem(**{**ready, "laser_disabled": True})
    assert "not loaded" in play_problem(**{**ready, "bank_loaded": False})
    assert "accept the sample" in play_problem(**{**ready, "sample_accepted": False})
    assert "playing" in play_problem(**ready, playing=True)
    assert "Laser Off" in play_problem(**ready, unknown=True)
    assert "setup step" in play_problem(**ready, busy=True)


class Rig:
    """The workflow as the actions see it: the Play metric answers each Arm (Armed, or Refused), and the line and
    every output are recorded in order."""

    def __init__(self, answer="Armed", fail=()):
        self.metric, self.answer, self.fail = "BankLoaded", answer, set(fail)
        self.sent: list[str] = []
        self.line_up = False

    def arm(self, request):
        self.sent.append(f"arm {request}")
        if "arm" in self.fail:
            raise TimeoutError("no reply")
        if self.answer:
            self.metric = f"{self.answer} {request.split(',')[0]}" if self.answer == "Armed" else f"Refused {self.answer}"

    def line(self, on):
        self.sent.append(f"line {'on' if on else 'off'}")
        if ("line on" if on else "line off") in self.fail:
            raise ConnectionError("agent busy")
        self.line_up = on

    def zero(self):
        self.sent.append("zero")
        if "zero" in self.fail:
            raise ConnectionError("agent busy")

    def play(self, **kw):
        return ConditionPlay(self.arm, self.line, self.zero, lambda: self.metric, poll_s=0.002, end_margin_s=0.01, **kw)


def wait_for(condition, timeout=2.0):
    end = time.monotonic() + timeout
    while not condition():
        assert time.monotonic() < end, "timed out"
        time.sleep(0.002)


PLAN = PlayPlan(2, 2.0, 0.05, 0.08)


def test_a_play_arms_raises_the_line_then_clears_it_and_zeroes_at_its_end():
    rig = Rig()
    play = rig.play()
    assert play.laser == "off"
    assert play.start(PLAN) == ""
    assert play.laser == "play"  # from the Arm: never "off" while the trial may play
    wait_for(lambda: play.state == "off")  # not "playing": its 0.08 s window can pass between two polls on a busy machine
    assert rig.sent == ["arm 2,2.0,0.05", "line on", "line off", "zero"]  # the line went up, then down, then the zero
    assert play.describe()["condition"] == 2
    assert play.laser == "off" and not rig.line_up and play.why == ""


def test_laser_off_mid_play_clears_the_line_and_zeroes_at_once():
    rig = Rig()
    play = rig.play()
    play.start(PlayPlan(1, 2.0, 5.0, 5.05))
    wait_for(lambda: play.state == "playing")
    started = time.monotonic()
    assert play.stop("Laser Off") == ""
    assert time.monotonic() - started < 2.5  # not the plan's 5 s
    assert play.state == "off" and play.laser == "off" and play.why == "Laser Off"
    assert rig.sent[-2:] == ["line off", "zero"] and not rig.line_up
    play.join(1)
    assert rig.sent.count("line on") == 1 and rig.sent.count("zero") == 1  # the thread sent nothing more


def test_a_stop_while_arming_never_raises_the_line():
    rig = Rig(answer="")  # the workflow has not answered yet
    play = rig.play()
    play.start(PLAN)
    wait_for(lambda: rig.sent)
    play.stop("Stop")
    rig.metric = "Armed 2"  # a late answer
    play.join(1)
    assert "line on" not in rig.sent and rig.sent[-2:] == ["line off", "zero"] and play.state == "off"


def test_a_play_the_workflow_refuses_raises_no_line():
    rig = Rig(answer="condition 2 is still armed or playing")
    play = rig.play()
    play.start(PLAN)
    wait_for(lambda: play.state == "off")
    assert "line on" not in rig.sent and "refused the play: condition 2 is still armed" in play.why


def test_a_play_not_armed_in_time_raises_no_line():
    rig = Rig(answer="")
    play = rig.play(arm_timeout_s=0.05)
    play.start(PLAN)
    wait_for(lambda: play.state == "off")
    assert "line on" not in rig.sent and "not armed within" in play.why


def test_an_arm_not_delivered_is_cleared_and_zeroed_all_the_same():
    rig = Rig(fail={"arm"})
    play = rig.play()
    play.start(PLAN)
    wait_for(lambda: play.state == "off")
    assert rig.sent == ["arm 2,2.0,0.05", "line off", "zero"] and "did not take the play" in play.why


def test_a_clear_not_delivered_leaves_the_laser_unknown_until_one_is():
    rig = Rig(fail={"line off"})
    play = rig.play()
    play.start(PlayPlan(1, 2.0, 5.0, 5.05))
    wait_for(lambda: play.state == "playing")
    assert "line's clear was not sent" in play.stop("Laser Off")
    assert play.state == "unknown" and play.laser == "unknown"  # never "off" while the line may be up
    assert "Laser Off" in play.start(PLAN)  # no new play meanwhile
    rig.fail.clear()
    assert play.retry() == ""
    assert play.laser == "off" and not rig.line_up


def test_a_line_not_raised_is_cleared_and_zeroed():
    rig = Rig(fail={"line on"})
    play = rig.play()
    play.start(PLAN)
    wait_for(lambda: play.state == "off")
    assert rig.sent[-2:] == ["line off", "zero"] and "not raised" in play.why


def test_one_play_at_a_time():
    rig = Rig()
    play = rig.play()
    play.start(PlayPlan(1, 2.0, 5.0, 5.05))
    wait_for(lambda: play.state == "playing")
    assert "condition 1 is playing" in play.start(PLAN)
    play.stop()
    assert play.start(PLAN) == ""
    wait_for(lambda: play.state == "off")
    assert rig.sent.count("arm 2,2.0,0.05") == 1


def test_a_stop_and_the_raise_are_never_interleaved():
    """Many stops racing the line's raise: whatever wins, the line ends down and the last action is the zero."""
    for _ in range(30):
        rig = Rig()
        play = rig.play()
        play.start(PlayPlan(1, 2.0, 5.0, 5.05))
        threading.Thread(target=play.stop, args=("Laser Off",)).start()
        play.join(1)
        wait_for(lambda: play.state == "off")
        assert not rig.line_up and rig.sent[-1] == "zero"


def test_the_rows_and_cycles_agree_with_the_apps_zapitbank_on_its_contract_bank():
    """ZapitBank.TrialRows and Cycles (ZapitDaqTests.EveryTrialIsFiniteAtTheCap) on the C# tests' own bank: at a 1 s cap
    (100000 rows at 100 kHz) condition 1 (10 ramp cycles) gets 30 cycles, 30 x 2500 + 25000 = the cap, as that test asserts;
    conditions 2 (no ramp) and 3 (4 ramp cycles) get 40 and 36 by the same formula."""
    path = Path(__file__).resolve().parents[2] / "src" / "OpenRigs.Zapit.Tests" / "Data" / "Settings_ZapitBank_T.json"
    if not path.is_file():
        pytest.skip("the C# test bank is not in this checkout")
    bank = json.loads(path.read_text(encoding="utf-8"))
    cap_rows = math.floor(1.0 * bank["sample_rate"] + 1e-6)
    assert cap_rows == 100000
    expected = {1: (30, 100000, 75000), 2: (40, 100000, 50000), 3: (36, 100000, 60000)}  # (cycles at the cap, its rows, rows of 20 cycles)
    for condition in bank["conditions"]:
        n = condition["scan_cycle"]["length"]
        cycles, rows, rows_of_20 = expected[condition["condition"]]
        for variant in condition["variants"]:
            assert most_cycles(cap_rows, n, variant) == cycles
            assert trial_rows(cycles, n, variant) == rows and trial_rows(cycles + 1, n, variant) > cap_rows
            assert trial_rows(20, n, variant) == rows_of_20
    assert plan_play(bank, 1, 1e9, 1.0, 3, power_mw=2.0).seconds == pytest.approx(0.75)  # 30 x 2500 rows at 100 kHz


@pytest.mark.parametrize("seconds", [float("inf"), float("-inf"), float("nan")])
def test_a_laser_duration_that_is_not_finite_is_refused(seconds):
    with pytest.raises(PlayRefused, match="not a number of seconds"):
        plan_play(index(), 1, 30, 6, seconds)


def test_a_buffer_that_is_not_finite_is_refused():
    with pytest.raises(PlayRefused, match="buffer is not known"):
        plan_play(index(), 1, 30, float("inf"), 1)


def test_a_huge_duration_is_shortened_at_once_to_the_cycles_that_fit():
    plan = plan_play(index(), 1, 30, buffer_seconds=1.0, task_seconds=1e6)
    assert plan.seconds == pytest.approx(0.95) and plan.trial_seconds <= 1.0


@pytest.mark.parametrize("condition", [True, 1.0, "1", None, -1])
def test_a_condition_that_is_not_a_whole_number_is_refused_by_name(condition):
    with pytest.raises(PlayRefused, match="the condition is"):
        plan_play(index(), condition, 30, 6, 1)
