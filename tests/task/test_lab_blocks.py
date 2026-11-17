import pytest
from pydantic import ValidationError

import re
from pathlib import Path

from open_rigs.core import task
from open_rigs.core.task import (
    MAX_REWARD_UL,
    AutoReward,
    BackgroundNoise,
    Iti,
    Optogenetics,
    Penalty,
    QuietWait,
    ResponseWindow,
    RewardSize,
    RewardTone,
    StimulusSound,
)

OPTO = {"laserOnProbability": 0.5, "laserToStimulusSeconds": 0.1, "trialsBeforeLaser": 0}


class Stage(QuietWait):
    label: str


def test_quiet_wait_keys_stay_on_the_stage():
    stage = Stage.model_validate({"label": "a", "lickQuietSeconds": 1, "wheelQuietSeconds": 0.5, "wheelQuietCm": 2})
    assert stage.model_dump(by_alias=True) == {"label": "a", "lickQuietSeconds": 1, "wheelQuietSeconds": 0.5, "wheelQuietCm": 2}


@pytest.mark.parametrize(("seconds", "cm"), [(1, 0), (0, 1)])
def test_quiet_wait_refuses_half_a_wheel_wait(seconds, cm):
    with pytest.raises(ValidationError, match="must both be above 0"):
        Stage(label="a", wheel_quiet_seconds=seconds, wheel_quiet_cm=cm)


# A wait of inf never reaches a trial's start and the run waits silently; a NaN lead passed check_lead_fits (every comparison false).
@pytest.mark.parametrize("key", ["lickQuietSeconds", "wheelQuietSeconds"])
def test_quiet_wait_refuses_an_endless_wait(key):
    with pytest.raises(ValidationError, match="finite number"):
        Stage.model_validate({"label": "a", key: float("inf"), "wheelQuietCm": 1})


def test_a_task_s_own_field_on_a_lab_block_is_finite_too():
    class TimedStage(QuietWait):
        response_window_seconds: float

    with pytest.raises(ValidationError, match="finite number"):
        TimedStage.model_validate({"responseWindowSeconds": float("inf")})


class Row(Iti, ResponseWindow, AutoReward, RewardSize):
    label: str


ROW = {"label": "a", "itiMinSeconds": 2, "itiMaxSeconds": 3, "autoRewardProbability": 0.5, "rewardSizeUl": 3}


def test_trial_blocks_keep_their_keys_where_the_task_puts_them():
    assert Row.model_validate(ROW).model_dump(by_alias=True) == {**ROW, "responseWindowSeconds": None}


def test_trial_blocks_have_no_lab_default_but_the_window():
    with pytest.raises(ValidationError) as error:
        Row.model_validate({"label": "a"})
    assert {e["loc"][0] for e in error.value.errors()} == {"itiMinSeconds", "itiMaxSeconds", "autoRewardProbability", "rewardSizeUl"}


@pytest.mark.parametrize(
    ("change", "message"),
    [
        ({"itiMaxSeconds": 1}, "iti_max_seconds must be >= iti_min_seconds"),
        ({"responseWindowSeconds": 0}, "greater than 0"),
        ({"autoRewardProbability": 1.5}, "less than or equal to 1"),
        ({"rewardSizeUl": MAX_REWARD_UL + 1}, f"less than or equal to {MAX_REWARD_UL}"),
        ({"itiMinSeconds": float("nan")}, "finite number"),
    ],
)
def test_trial_blocks_refuse(change, message):
    with pytest.raises(ValidationError, match=message):
        Row.model_validate({**ROW, **change})


def test_penalty_refuses_an_endless_timeout():
    with pytest.raises(ValidationError, match="finite number"):
        Penalty.model_validate({"timeoutSeconds": float("inf")})


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


def test_penalty_sound_is_on_by_default_and_the_timeout_has_no_lab_default():
    penalty = Penalty.model_validate({"timeoutSeconds": 2})
    assert penalty.sound.enabled and penalty.sound.level_dbfs == -30
    with pytest.raises(ValidationError, match="timeoutSeconds"):
        Penalty.model_validate({})


def test_sound_levels_have_the_lab_defaults_in_dbfs_rms():
    # The lab's defaults: stimuli -30, penalty -30, reward tone -36, background -50; each task file states its own.
    assert (StimulusSound().level_dbfs, RewardTone().level_dbfs, BackgroundNoise().level_dbfs) == (-30, -36, -50)
    assert BackgroundNoise().enabled
    assert set(BackgroundNoise.model_json_schema(by_alias=True)["properties"]) == {"enabled", "levelDbfs"}
    with pytest.raises(ValidationError, match="less than or equal to -1"):
        StimulusSound(level_dbfs=-0.5)


def test_a_level_s_help_says_its_peak_is_checked_as_its_buffer_is_built():
    # A buffer built each trial is checked at that trial, not when the task file is read at Go.
    help_text = StimulusSound.model_json_schema(by_alias=True)["properties"]["levelDbfs"]["description"]
    assert "at its trial for one built each trial" in help_text
    assert "when the task file is read" not in help_text


def test_the_levels_are_open_rigs_audio_s():
    # OpenRigs.Audio SoundLevel plays every sound; one set of numbers.
    source = (Path(__file__).parents[2] / "src" / "OpenRigs.Audio" / "SoundLevel.cs").read_text(encoding="utf-8")
    consts = {name: float(value) for name, value in re.findall(r"public const double (\w+) = (-?[\d.]+);", source)}
    assert consts == {
        "StimulusDbfs": task.STIMULUS_LEVEL_DBFS,
        "PenaltyDbfs": task.PENALTY_LEVEL_DBFS,
        "RewardToneDbfs": task.REWARD_TONE_LEVEL_DBFS,
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
    with pytest.raises(ValueError, match=r"trigger_hold_seconds \(3.0 s\) must be at most 1.3 s"):
        on.check_zapit_free_in_time(2.0, trigger_hold_seconds=3.0)
    on.check_zapit_free_in_time(2.0, trigger_hold_seconds=1.3)
    with pytest.raises(ValueError, match=r"laser_duration_seconds \(2.0 s\) must be at most 1.8 s"):
        on.check_zapit_free_in_time(2.0, laser_duration_seconds=2.0, ramp_down_seconds=0.5, response_delay_seconds=0.5)
    on.check_zapit_free_in_time(2.0, laser_duration_seconds=1.8, ramp_down_seconds=0.5, response_delay_seconds=0.5)
    Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": 0.5}).check_zapit_free_in_time(2.0, trigger_hold_seconds=30)  # Zapit off
    with pytest.raises(TypeError):
        on.check_zapit_free_in_time(2.0)


def test_a_level_s_help_says_overlapping_sounds_share_a_speaker_s_headroom():
    # Each buffer's peak is checked as it is built; two sounds playing at once on one speaker add up.
    help_text = StimulusSound.model_json_schema(by_alias=True)["properties"]["levelDbfs"]["description"]
    assert "Sounds overlapping on a speaker share its headroom: each buffer's peak is checked, not their mix" in help_text
