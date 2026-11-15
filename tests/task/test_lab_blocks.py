import pytest
from pydantic import ValidationError

from open_rigs.core.task import Optogenetics, Penalty, QuietWait

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


def test_optogenetics_needs_a_file_and_a_power_when_on():
    assert not Optogenetics.model_validate(OPTO).use_zapit
    with pytest.raises(ValidationError, match="zapit_config_path is empty"):
        Optogenetics.model_validate({**OPTO, "useZapit": True, "laserPowerMw": 3})
    with pytest.raises(ValidationError, match="laser_power_mw is not set"):
        Optogenetics.model_validate({**OPTO, "useZapit": True, "zapitConfigPath": "c.yml"})
    with pytest.raises(ValidationError, match="finite"):
        Optogenetics.model_validate({**OPTO, "laserPowerMw": float("inf")})


def test_optogenetics_has_no_lab_default_for_the_design():
    with pytest.raises(ValidationError) as error:
        Optogenetics.model_validate({})
    assert {e["loc"][0] for e in error.value.errors()} == {"laserOnProbability", "laserToStimulusSeconds", "trialsBeforeLaser"}


def test_optogenetics_schema_holds_the_same_rule():
    # The launching app checks a task file against the schema, not the model.
    schema = Optogenetics.model_json_schema(by_alias=True)
    assert schema["if"]["properties"] == {"useZapit": {"const": True}}
    assert schema["then"]["required"] == ["laserPowerMw"]
    assert schema["then"]["properties"]["zapitConfigPath"] == {"minLength": 1}


def test_penalty_sound_is_on_by_default_and_the_timeout_has_no_lab_default():
    penalty = Penalty.model_validate({"timeoutSeconds": 2})
    assert penalty.sound.enabled and penalty.sound.gain == 1
    with pytest.raises(ValidationError, match="timeoutSeconds"):
        Penalty.model_validate({})


@pytest.mark.parametrize("lead, fits", [(3.2, True), (3.3, False), (-1.0, True)])
def test_optogenetics_lead_and_reply_margin_fit_in_the_shortest_interval(lead, fits):
    opto = Optogenetics.model_validate({**OPTO, "laserToStimulusSeconds": lead})
    if fits:
        opto.check_lead_fits(3.4)
    else:
        with pytest.raises(ValueError, match=r"reply margin must be at most trial_structure.iti_min_seconds \(3.4 s\)"):
            opto.check_lead_fits(3.4)
