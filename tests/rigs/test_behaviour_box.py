import pytest
from pydantic import ValidationError

from open_rigs.rigs.behaviour_box import BehaviourBoxRig

RIG = {
    "rootPath": "C:\\Data\\B1",
    "rigId": "B1",
    "behaviorBoard": {"main": {"portName": "COM3", "usbSerial": "AB0M7PLB"}},
    "camera": {"box": {"serialNumber": "12345"}},
}


def test_named_entries():
    rig = BehaviourBoxRig.model_validate(RIG)
    assert rig.behavior_board["main"].usb_serial == "AB0M7PLB"
    assert rig.camera["box"].live_view is None


def test_no_version():
    assert "version" not in BehaviourBoxRig.model_fields
    assert "version" not in BehaviourBoxRig.model_validate(RIG).model_dump(by_alias=True)


def test_usb_serial_required():
    with pytest.raises(ValidationError):
        BehaviourBoxRig.model_validate({**RIG, "behaviorBoard": {"main": {"portName": "COM3"}}})
