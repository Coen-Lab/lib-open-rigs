import pytest
from pydantic import ValidationError

import re
from pathlib import Path

from open_rigs.core import task
from open_rigs.core.task import (
    MAX_REWARD_UL,
    AutoReward,
    Iti,
    Optogenetics,
    Penalty,
    LickQuietWait,
    ResponseWindow,
    Reward,
    RewardTone,
    Spouts,
    StimulusTiming,
    WheelQuietWait,
)

OPTO = {"laserOnProbability": 0.5, "laserToStimulusSeconds": 0.1, "trialsBeforeLaser": 0}


class Stage(LickQuietWait):
    label: str


class Structure(WheelQuietWait):
    label: str


def test_each_quiet_wait_keeps_its_keys_where_the_task_puts_it():
    # A task puts each part on its stages if a stage changes it, else at task level, under the same keys.
    stage = Stage.model_validate({"label": "a", "lickQuietSeconds": 1})
    assert stage.model_dump(by_alias=True) == {"label": "a", "lickQuietSeconds": 1}
    structure = Structure.model_validate({"label": "a", "wheelQuietSeconds": 0.5, "wheelQuietCm": 2})
    assert structure.model_dump(by_alias=True) == {"label": "a", "wheelQuietSeconds": 0.5, "wheelQuietCm": 2}


@pytest.mark.parametrize(("seconds", "cm"), [(1, 0), (0, 1)])
def test_wheel_quiet_seconds_0_is_the_only_off_and_cm_0_is_no_movement(seconds, cm):
    # P5: no pair rule; wheelQuietCm 0 with a wait asks for a still wheel.
    Structure(label="a", wheel_quiet_seconds=seconds, wheel_quiet_cm=cm)


# A wait of inf never reaches a trial's start and the run waits silently; a NaN lead passed check_lead_fits (every comparison false).
@pytest.mark.parametrize(("block", "key"), [(Stage, "lickQuietSeconds"), (Structure, "wheelQuietSeconds")])
def test_quiet_wait_refuses_an_endless_wait(block, key):
    with pytest.raises(ValidationError, match="finite number"):
        block.model_validate({"label": "a", key: float("inf"), "wheelQuietCm": 1})


def test_a_task_s_own_field_on_a_lab_block_is_finite_too():
    class TimedStage(LickQuietWait):
        response_window_seconds: float

    with pytest.raises(ValidationError, match="finite number"):
        TimedStage.model_validate({"responseWindowSeconds": float("inf")})


class Row(Iti, ResponseWindow, AutoReward):
    label: str
    reward: Reward


ROW = {"label": "a", "itiMinSeconds": 2, "itiMaxSeconds": 3, "autoRewardProbability": 0.5, "reward": {"sizeUl": 3}}


def test_trial_blocks_keep_their_keys_where_the_task_puts_them():
    assert Row.model_validate(ROW).model_dump(by_alias=True) == {**ROW, "responseWindowSeconds": None, "reward": {"sizeUl": 3, "largeMultiplier": 2.0, "largeProbability": 0.0, "tone": {"levelDbfs": "mute"}}}


def test_trial_blocks_have_no_lab_default_but_the_window():
    with pytest.raises(ValidationError) as error:
        Row.model_validate({"label": "a"})
    assert {e["loc"][0] for e in error.value.errors()} == {"itiMinSeconds", "itiMaxSeconds", "autoRewardProbability", "reward"}


@pytest.mark.parametrize(
    ("change", "message"),
    [
        ({"itiMaxSeconds": 1}, "iti_max_seconds must be >= iti_min_seconds"),
        ({"responseWindowSeconds": 0}, "greater than 0"),
        ({"autoRewardProbability": 1.5}, "less than or equal to 1"),
        ({"reward": {"sizeUl": MAX_REWARD_UL + 1}}, f"less than or equal to {MAX_REWARD_UL}"),
        ({"itiMinSeconds": float("nan")}, "finite number"),
    ],
)
def test_trial_blocks_refuse(change, message):
    with pytest.raises(ValidationError, match=message):
        Row.model_validate({**ROW, **change})


def test_penalty_refuses_an_endless_timeout():
    with pytest.raises(ValidationError, match="finite number"):
        Penalty.model_validate({"incorrect": {"timeoutSeconds": float("inf")}})


@pytest.mark.parametrize("lead", [float("nan"), float("-inf")])
def test_optogenetics_refuses_a_lead_that_is_not_a_number(lead):
    with pytest.raises(ValidationError, match="finite number"):
        Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": lead})


def test_optogenetics_needs_a_file_and_a_power_when_on():
    assert not Optogenetics.model_validate(OPTO).enabled
    with pytest.raises(ValidationError, match="zapit_conditions_path is empty"):
        Optogenetics.model_validate({**OPTO, "enabled": True, "laserPowerMw": 3})
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        Optogenetics.model_validate({**OPTO, "enabled": True, "zapitConditionsPath": "c.yml"})
    with pytest.raises(ValidationError, match="finite"):
        Optogenetics.model_validate({**OPTO, "laserPowerMw": float("inf")})


def test_optogenetics_has_no_lab_default_for_the_design():
    with pytest.raises(ValidationError) as error:
        Optogenetics.model_validate({})
    assert {e["loc"][0] for e in error.value.errors()} == {"laserOnProbability", "laserToStimulusSeconds", "trialsBeforeLaser"}


def test_optogenetics_schema_holds_the_same_rule():
    # The launching app checks a task file against the schema, not the model.
    schema = Optogenetics.model_json_schema(by_alias=True)
    assert schema["if"]["properties"] == {"enabled": {"const": True}}
    assert schema["then"]["required"] == ["laserPowerMw"]
    assert schema["then"]["properties"]["zapitConditionsPath"] == {"minLength": 1}


def test_penalty_kinds_default_to_4_s_and_minus_35_dbfs_and_mute_is_off():
    penalty = Penalty.model_validate({})
    assert penalty.model_dump(by_alias=True) == {
        "incorrect": {"timeoutSeconds": 4.0, "sound": {"levelDbfs": -35.0}, "flash": {"brightness": 0.0, "rateHz": 10.0}},
        "noResponse": {"timeoutSeconds": 4.0, "sound": {"levelDbfs": -35.0}, "flash": {"brightness": 0.0, "rateHz": 10.0}},
    }
    assert set(Penalty.model_json_schema(by_alias=True)["$defs"]["PenaltySound"]["properties"]) == {"levelDbfs"}
    assert Penalty.model_validate({"incorrect": {"sound": {"levelDbfs": "mute"}}}).incorrect.sound.level_dbfs == "mute"
    for refused in ("off", False, -0.5):
        with pytest.raises(ValidationError):
            Penalty.model_validate({"incorrect": {"sound": {"levelDbfs": refused}}})
    assert set(Penalty.model_fields) == {"incorrect", "no_response"}  # no old keys kept


def test_each_penalty_kind_has_its_own_full_values_and_no_response_may_be_null():
    miss = Penalty.model_validate({"incorrect": {"timeoutSeconds": 2}, "noResponse": {"timeoutSeconds": 0, "sound": {"levelDbfs": "mute"}}}).no_response
    assert (miss.timeout_seconds, miss.sound.level_dbfs) == (0, "mute")
    partial = Penalty.model_validate({"noResponse": {"timeoutSeconds": 1}}).no_response
    assert (partial.timeout_seconds, partial.sound.level_dbfs) == (1, -35), "a left-out field is the lab's default, never the other kind's"
    for refused in ({"timeoutSeconds": -1}, {"sound": {"levelDbfs": "off"}}, {"sound": {"levelDbfs": 0}}):
        with pytest.raises(ValidationError):
            Penalty.model_validate({"noResponse": refused})
    never = Penalty.model_validate({"noResponse": None})
    assert never.no_response is None
    never.check_misses(False)
    with pytest.raises(ValueError, match="penalty.no_response is null, but this task penalises a miss"):
        never.check_misses(True)
    Penalty().check_misses(True)


def test_a_penalty_flash_is_off_by_default_and_held_to_its_rules():
    # brightness 0 to 1 of full white (0: no flash, the default), rateHz above 0 and at most 30 Hz (half the 60 Hz render loop)
    flash = Penalty.model_validate({"noResponse": {"flash": {"brightness": 1, "rateHz": 30}}}).no_response.flash
    assert (flash.brightness, flash.rate_hz) == (1, 30)
    assert Penalty().incorrect.flash.brightness == 0
    for refused in ({"brightness": 1.5}, {"brightness": -0.1}, {"brightness": float("nan")}, {"rateHz": 0}, {"rateHz": 31}, {"rateHz": float("inf")}):
        with pytest.raises(ValidationError):
            Penalty.model_validate({"incorrect": {"flash": refused}})
    assert task.PENALTY_FLASH_MAX_RATE_HZ == 30 and task.PENALTY_FLASH_RATE_HZ == 10  # OpenRigs.Trials PenaltyFlash.MaxRateHz, DefaultRateHz


def test_reward_holds_its_tone_and_no_consumption():
    reward = Reward.model_validate({"sizeUl": 3})
    assert reward.model_dump(by_alias=True) == {
        "sizeUl": 3.0, "largeMultiplier": 2.0, "largeProbability": 0.0, "tone": {"levelDbfs": "mute"}}
    assert Reward.model_validate({"sizeUl": 3, "tone": {"levelDbfs": -20}}).tone.level_dbfs == -20
    assert "consumption_seconds" not in Reward.model_fields  # spouts.rewardConsumptionSeconds
    for refused in ({"tone": {"levelDbfs": "off"}},):
        with pytest.raises(ValidationError):
            Reward.model_validate({"sizeUl": 3, **refused})

def test_sound_levels_have_the_lab_defaults_in_dbfs_rms():
    # The lab's defaults: stimuli -30, penalty -35, reward tone mute; the background a lab constant, -60 (P15).
    assert (task.STIMULUS_LEVEL_DBFS, Penalty().incorrect.sound.level_dbfs, RewardTone().level_dbfs, task.BACKGROUND_LEVEL_DBFS) == (-30, -35, "mute", -60)
    assert not hasattr(task, "BackgroundNoise") and not hasattr(task, "StimulusSound")


def test_a_level_s_schema_is_a_number_or_mute():
    level = RewardTone.model_json_schema(by_alias=True)["properties"]["levelDbfs"]
    assert level["oneOf"] == [{"type": "number", "minimum": -120.0, "maximum": -1.0}, {"const": "mute", "type": "string"}]
    assert "silent stretches" in level["description"] and "mute: no sound" in level["description"]


def test_the_levels_are_open_rigs_audio_s():
    # OpenRigs.Audio SoundLevel plays every sound; one set of numbers.
    source = (Path(__file__).parents[2] / "src" / "OpenRigs.Audio" / "SoundLevel.cs").read_text(encoding="utf-8")
    consts = {name: float(value) for name, value in re.findall(r"public const double (\w+Dbfs) = (-?[\d.]+);", source)}
    assert consts == {
        "StimulusDbfs": task.STIMULUS_LEVEL_DBFS,
        "PenaltyDbfs": task.PENALTY_LEVEL_DBFS,
        "BackgroundDbfs": task.BACKGROUND_LEVEL_DBFS,
        "PeakLimitDbfs": task.PEAK_LIMIT_DBFS,
    }


@pytest.mark.parametrize("lead, fits", [(3.2, True), (3.3, False), (-1.0, True)])
def test_optogenetics_lead_and_reply_margin_fit_in_the_shortest_interval(lead, fits):
    opto = Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": lead})
    if fits:
        opto.check_lead_fits(3.4)
    else:
        with pytest.raises(ValueError, match=r"reply margin must be at most trial_structure.iti_min_seconds \(3.4 s\)"):
            opto.check_lead_fits(3.4)


def test_zapit_must_be_free_before_the_next_trigger_less_the_reply_margin():
    # A 3 s hold, a 2 s shortest interval and a 0.5 s lead passed every task check and stopped at trial 2.
    on = Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": 0.5, "enabled": True, "zapitConditionsPath": "z.yml", "laserPowerMw": 2})
    with pytest.raises(ValueError, match=r"laser_duration_seconds \(2.0 s\) must be at most 1.8 s"):
        on.check_zapit_free_in_time(2.0, laser_duration_seconds=2.0, ramp_down_seconds=0.5, response_delay_seconds=0.5)
    on.check_zapit_free_in_time(2.0, laser_duration_seconds=1.8, ramp_down_seconds=0.5, response_delay_seconds=0.5)
    Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": 0.5}).check_zapit_free_in_time(2.0, laser_duration_seconds=30)  # Zapit off
    with pytest.raises(TypeError):
        on.check_zapit_free_in_time(2.0, trigger_hold_seconds=1.0)


def test_a_laser_until_the_outcome_holds_the_line_through_the_files_ramp_down():
    from open_rigs.components.zapit import RAMP_DOWN_CONTEXT, ramp_down_from

    on = Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": 0.5, "enabled": True, "zapitConditionsPath": "z.yml", "laserPowerMw": 2})
    with pytest.raises(ValueError, match=r"ramp-down \(0.5 s\) must be at most 0.3 s"):
        on.check_ramp_down_fits(1.0, 0.5)
    on.check_ramp_down_fits(1.0, 0.3)
    Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": 0.5}).check_ramp_down_fits(1.0, 0.5)  # Zapit off
    assert ramp_down_from(None) == 0 and ramp_down_from({RAMP_DOWN_CONTEXT: 0.4}) == 0.4


def test_a_level_s_help_says_overlapping_sounds_share_a_speaker_s_headroom():
    # Each buffer's peak is checked as it is built; two sounds playing at once on one speaker add up.
    help_text = RewardTone.model_json_schema(by_alias=True)["properties"]["levelDbfs"]["description"]
    assert "Sounds overlapping on a speaker share its headroom: each buffer's peak is checked, not their mix" in help_text


def test_spouts_keys_default_and_rules():
    spouts = Spouts.model_validate({"stimulusToSpoutsInSeconds": 0.5})
    assert spouts.model_dump(by_alias=True) == {"alwaysIn": False, "stimulusToSpoutsInSeconds": 0.5, "rewardConsumptionSeconds": 1.5}
    spouts.check_fits(1.5, 0.5)
    with pytest.raises(ValueError, match=r"reward_consumption_seconds \(1.5 s\) is above iti_min_seconds \(1.4 s\)"):
        spouts.check_fits(1.4, 0.5)
    with pytest.raises(ValueError, match="response window would open before the spouts arrive"):
        spouts.check_fits(3, 0.4)
    for refused in ({"stimulusToSpoutsInSeconds": float("inf")}, {"stimulusToSpoutsInSeconds": 0.5, "rewardConsumptionSeconds": -1}):
        with pytest.raises(ValidationError):
            Spouts.model_validate(refused)
    early = Spouts.model_validate({"stimulusToSpoutsInSeconds": -1.0})
    early.check_fits(2.5, 0)  # signed: no response window rule before the stimulus; 1.5 + 1 fits 2.5
    with pytest.raises(ValueError, match=r"reward_consumption_seconds \(1.5 s\) plus the 1.0 s the spouts come in before the stimulus is above iti_min_seconds \(2.4 s\)"):
        early.check_fits(2.4, 0)
    Spouts.model_validate({"stimulusToSpoutsInSeconds": -1.0, "alwaysIn": True}).check_fits(2.4, 0)  # always in: no lead
    assert task.SPOUTS_REWARD_CONSUMPTION_SECONDS == 1.5  # OpenRigs.Trials Spouts.DefaultRewardConsumptionSeconds

def test_stimulus_timing_keys_default_and_rules():
    timing = StimulusTiming.model_validate({"durationSeconds": 1.5})
    assert timing.model_dump(by_alias=True) == {"durationSeconds": 1.5, "responseToStimulusOffSeconds": None}
    assert StimulusTiming.model_validate({"durationSeconds": 1.5, "responseToStimulusOffSeconds": 0}).response_to_stimulus_off_seconds == 0
    soonest = {"stimulus_to_earliest_outcome_seconds": 0.0, "min_outcome_to_iti_seconds": 0.0}
    timing.check_fits(1.5, 1.5, **soonest)
    with pytest.raises(ValueError, match=r"duration_seconds \(1.5 s\) runs 1.5 s past the soonest outcome, longer than .* plus iti_min_seconds \(1.4 s\)"):
        timing.check_fits(1.4, **soonest)
    # The window opening 0.5 s after the stimulus, itiMinSeconds 1, probes possible: the next stimulus at the soonest 1.5 s after.
    timing.check_fits(1.0, stimulus_to_earliest_outcome_seconds=0.5, min_outcome_to_iti_seconds=0.0)
    with pytest.raises(ValueError, match="past the soonest outcome"):
        StimulusTiming.model_validate({"durationSeconds": 1.6}).check_fits(1.0, stimulus_to_earliest_outcome_seconds=0.5, min_outcome_to_iti_seconds=0.0)
    with pytest.raises(ValueError, match="response window's close"):
        timing.check_fits(3, 1, **soonest)
    with pytest.raises(TypeError):
        timing.check_fits(3, 1, 0.0)  # the soonest outcome and the outcome to the interval are named, never positional
    late = StimulusTiming.model_validate({"durationSeconds": 1, "responseToStimulusOffSeconds": 2.5})
    late.check_fits(2, stimulus_to_earliest_outcome_seconds=0.0, min_outcome_to_iti_seconds=0.5)
    with pytest.raises(ValueError, match=r"response_to_stimulus_off_seconds \(2.5 s\) is longer than the shortest outcome to next interval \(0.0 s"):
        late.check_fits(2, **soonest)
    StimulusTiming.model_validate({"responseToStimulusOffSeconds": 0.5}).check_fits(1, **soonest)  # no duration: no cap, its checks skipped
    with pytest.raises(ValueError, match="both null: the stimulus would never end"):
        StimulusTiming.model_validate({}).check_fits(1, **soonest)
    for bad in ({"durationSeconds": 0}, {"durationSeconds": float("inf")}, {"durationSeconds": 1, "responseToStimulusOffSeconds": -1}):
        with pytest.raises(ValidationError):
            StimulusTiming.model_validate(bad)



def test_max_reward_ul_is_the_csharp_default():
    source = (Path(__file__).parents[2] / "src" / "OpenRigs.Core" / "RewardLine.cs").read_text(encoding="utf-8")
    match = re.search(r"public const double DefaultMaxRewardUl = ([0-9.]+);", source)
    assert match, "RewardLine.cs has no DefaultMaxRewardUl"
    assert float(match.group(1)) == MAX_REWARD_UL


class AlwaysFullRow(Iti, ResponseWindow, task.ResponseWindowAlwaysFull):
    label: str


def test_an_always_full_window_is_on_by_default_in_a_task_with_the_block_and_needs_a_limit():
    row = {"label": "a", "itiMinSeconds": 2, "itiMaxSeconds": 3}
    assert AlwaysFullRow.model_validate({**row, "responseWindowSeconds": 1.5}).response_window_always_full is True
    assert AlwaysFullRow.model_validate({**row, "responseWindowAlwaysFull": False}).response_window_always_full is False
    with pytest.raises(ValidationError, match="response_window_always_full needs a response_window_seconds"):
        AlwaysFullRow.model_validate(row)
    with pytest.raises(ValidationError, match="response_window_always_full needs a response_window_seconds"):
        AlwaysFullRow.model_validate({**row, "responseWindowAlwaysFull": True})


def _combination(iti: float | None = 2.5, to_window=0.5, window: float | None = 2.0, lead=0.1, always_full=True, label=None):
    return {**({"label": label} if label else {}),
            "trialStructure": {"itiMinSeconds": iti, "stimulusToResponseWindowSeconds": to_window, "responseWindowSeconds": window,
                               "responseWindowAlwaysFull": always_full},
            "optogenetics": {"laserToStimulusSeconds": lead, "laserDurationSeconds": 2.5}}


def test_zapit_trial_seconds_is_the_shortest_trigger_to_trigger_less_the_ramp_down_and_arm_time():
    # ZapitDaq's buffer from the standard blocks, OptoLimits' rule (decision-making's _between_trials).
    from open_rigs.core.task import zapit_trial_seconds
    # decision-making's own file: 0.1 + 2.5 + (2.5 - 0.1) = 5 s trigger to trigger, less 0.25 s ramp-down and 0.2 s arm time
    assert zapit_trial_seconds([_combination()], 0.25) == (pytest.approx(4.55), "")
    # Boundary: a 2.5 s laser and its 0.25 s ramp fill a 2.75 s buffer exactly (0.1 + 1.2 + 1.9 = 3.2 s trigger to trigger)
    assert zapit_trial_seconds([_combination(iti=2.0, to_window=0.2, window=1.0)], 0.25) == (pytest.approx(2.75), "")
    # No responseWindowSeconds: the trial ends at the window's opening (TrialEndSeconds)
    assert zapit_trial_seconds([_combination(window=None)], 0.0)[0] == pytest.approx(0.1 + 0.5 + 2.4 - 0.2)


@pytest.mark.parametrize("lead, iti, to_window, window, ramp_down, buffer", [
    (0.1, 2.5, 0.5, 2.0, 0.25, 4.55), (0.1, 2.0, 0.2, 1.0, 0.25, 2.75), (3.2, 3.4, 0.5, 2.0, 0.5, 5.2), (3.3, 3.4, 0.5, 2.0, 0.5, None)])
def test_zapit_trial_seconds_agrees_with_optolimits(lead, iti, to_window, window, ramp_down, buffer):
    # The C# rule this copies: OpenRigs.Trials.Tests OptoTests.OptoLimitsGiveTheBuffersPythonsZapitTrialSecondsGives
    # works out the same buffers for the same cases from OptoLimits (None: LeadProblem refuses the lead).
    from open_rigs.core.task import zapit_trial_seconds
    seconds, why = zapit_trial_seconds([_combination(iti=iti, to_window=to_window, window=window, lead=lead)], ramp_down)
    if buffer is None:
        assert seconds == 0.0 and "arm time" in why
    else:
        assert (seconds, why) == (pytest.approx(buffer), "")


def test_zapit_trial_seconds_takes_the_shortest_of_every_combination_a_session_may_draw():
    from open_rigs.core.task import zapit_trial_seconds
    # A stage row with a shorter interval sets the buffer for the session, whichever stage it starts on
    rows = [_combination(), _combination(iti=1.5)]
    assert zapit_trial_seconds(rows, 0.25)[0] == pytest.approx(0.1 + 2.5 + 1.4 - 0.45)
    assert zapit_trial_seconds(list(reversed(rows)), 0.25)[0] == pytest.approx(3.55)
    # A negative lead (the trigger after the stimulus onset): the shortest lead plus trial end, plus the shortest
    # interval less the lead, each over the combinations
    assert zapit_trial_seconds([_combination(lead=-0.3)], 0.25)[0] == pytest.approx(-0.3 + 2.5 + 2.8 - 0.45)
    mixed = [_combination(lead=0.1), _combination(lead=-0.3)]
    assert zapit_trial_seconds(mixed, 0.25)[0] == pytest.approx(min(2.6, 2.2) + min(2.4, 2.8) - 0.45)


def test_zapit_trial_seconds_says_why_a_session_cannot_run():
    from open_rigs.core.task import zapit_trial_seconds
    # OptoLimits.LeadProblem: the lead plus the 0.2 s arm time within the combination's own interval
    seconds, why = zapit_trial_seconds([_combination(), _combination(iti=1.5, lead=1.4)], 0.25)
    assert seconds == 0.0 and why == ("trialStructure.itiMinSeconds: the shortest interval in the Task's Own Parameters is 1.5 s, but "
                                      "optogenetics.laserToStimulusSeconds (1.4 s) plus Zapit's 0.2 s arm time must fit within it: "
                                      "lengthen the interval or shorten the lead")
    why = zapit_trial_seconds([_combination(iti=1.5, lead=1.4, label="Stage Two")], 0.25)[1]
    assert why.startswith("trialStructure.itiMinSeconds: the shortest interval in Stage Stage Two is 1.5 s")
    assert zapit_trial_seconds([_combination(iti=1.5, lead=1.3)], 0.25)[1] == ""  # exactly fits
    no_buffer = zapit_trial_seconds([_combination(iti=0.3, to_window=0.1, window=0.0, lead=0.1)], 0.5)[1]
    assert "no Zapit trial buffer" in no_buffer and no_buffer.startswith("trialStructure.itiMinSeconds: ")
    assert no_buffer.endswith("lengthen it or shorten the conditions file's ramp-down")
    assert "trialStructure.itiMinSeconds: it is None" in zapit_trial_seconds([_combination(iti=None)], 0.25)[1]
    assert "laserToStimulusSeconds: it is True" in zapit_trial_seconds([_combination(lead=True)], 0.25)[1]
    assert "no trial combination" in zapit_trial_seconds([], 0.25)[1]


def test_zapit_does_not_allow_a_laser_that_runs_until_stopped():
    # No laserDurationSeconds and no laserCapSeconds (null, absent or 0) is refused, naming the stage row.
    from open_rigs.core.task import zapit_open_ended_laser
    assert zapit_open_ended_laser([_combination()]) == ""
    assert zapit_open_ended_laser([{"optogenetics": {"laserDurationSeconds": 0, "laserCapSeconds": 2.0}}]) == ""  # a cap bounds it
    for blank in ({}, {"laserDurationSeconds": None}, {"laserDurationSeconds": 0, "laserCapSeconds": 0}):
        assert zapit_open_ended_laser([_combination(), {"label": "S2", "optogenetics": blank}]) == (
            "optogenetics.laserDurationSeconds: Zapit needs a laser duration (or a laserCapSeconds), and none is set in Stage S2")
    assert zapit_open_ended_laser([{"optogenetics": {}}]).endswith("in the Task's Own Parameters")
    assert zapit_open_ended_laser([{"optogenetics": {"enabled": False}}]) == ""  # optogenetics off asks nothing


def test_zapit_trial_seconds_drops_the_response_window_only_when_always_full_is_explicitly_false():
    from open_rigs.core.task import zapit_trial_seconds
    # 0.1 + 0.5 + (2.5 - 0.1) = 3 s trigger to trigger without the 2 s window, less 0.25 s ramp-down and 0.2 s arm time
    assert zapit_trial_seconds([_combination(always_full=False)], 0.25)[0] == pytest.approx(2.55)
    assert zapit_trial_seconds([_combination(always_full=True)], 0.25)[0] == pytest.approx(4.55)
    # Real task files omit the field (the schema fixes it to true): absent is the same as true
    absent = _combination()
    del absent["trialStructure"]["responseWindowAlwaysFull"]
    assert zapit_trial_seconds([absent], 0.25) == zapit_trial_seconds([_combination(always_full=True)], 0.25)


@pytest.mark.parametrize("ramp", [float("nan"), float("inf"), -float("inf"), -2.0])
def test_zapit_trial_seconds_refuses_a_ramp_down_that_is_not_a_finite_number_of_zero_or_more(ramp):
    from open_rigs.core.task import zapit_trial_seconds
    seconds, why = zapit_trial_seconds([_combination()], ramp)
    assert seconds == 0.0 and "ramp-down" in why


@pytest.mark.parametrize("ramp_ms", [".nan", ".inf", "-2000"])
def test_max_ramp_down_seconds_refuses_a_ramp_down_that_is_not_finite_or_is_negative(tmp_path, ramp_ms):
    from open_rigs.components.zapit import max_ramp_down_seconds
    path = tmp_path / "conditions.yml"
    path.write_text(f"stimLocations1:\n  offRampDownDuration_ms: {ramp_ms}\n", encoding="utf-8")
    with pytest.raises(ValueError, match="stimLocations1.*offRampDownDuration_ms"):
        max_ramp_down_seconds(path)
    path.write_text("stimLocations1:\n  offRampDownDuration_ms: 250\n", encoding="utf-8")
    assert max_ramp_down_seconds(path) == pytest.approx(0.25)
