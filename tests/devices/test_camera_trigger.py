import pytest
from pydantic import ValidationError
from open_rigs.devices.behavior_board import CameraTriggerController


def test_only_trigger0_is_enabled_by_default():
    controller = CameraTriggerController.model_validate({"trigger0Frequency": 50, "trigger1Frequency": 50})
    assert controller.trigger0_enabled
    assert not controller.trigger1_enabled


def test_both_frequencies_are_required():
    with pytest.raises(ValidationError):
        CameraTriggerController.model_validate({"trigger0Frequency": 50})


def test_flags_serialise_in_camel_case():
    controller = CameraTriggerController(trigger0_frequency=30, trigger1_frequency=50, trigger1_enabled=True)
    data = controller.model_dump(by_alias=True)
    assert data["trigger0Enabled"] is True
    assert data["trigger1Enabled"] is True
    assert data["trigger0Frequency"] == 30
