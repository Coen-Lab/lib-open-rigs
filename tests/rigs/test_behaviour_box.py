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


def test_pixel_format_mono8_unless_set():
    assert BehaviourBoxRig.model_validate(RIG).camera["box"].pixel_format == "Mono8"
    rig = BehaviourBoxRig.model_validate({**RIG, "camera": {"box": {"serialNumber": "12345", "pixelFormat": "Mono16"}}})
    assert rig.camera["box"].pixel_format == "Mono16"


def test_no_version():
    assert "version" not in BehaviourBoxRig.model_fields
    assert "version" not in BehaviourBoxRig.model_validate(RIG).model_dump(by_alias=True)


def test_usb_serial_required():
    with pytest.raises(ValidationError):
        BehaviourBoxRig.model_validate({**RIG, "behaviorBoard": {"main": {"portName": "COM3"}}})


def test_wiring_by_channel_name():
    board = {
        "portName": "COM6",
        "usbSerial": "BG01WA0E",
        "wiring": {
            "DIPort0": {"device": "Photodiode", "note": "sync square"},
            "DO0": {"device": "CameraTrigger", "targets": ["Camera-Box"]},
            "DO1": {"device": "RewardValve", "side": "left", "part": "?"},
        },
    }
    rig = BehaviourBoxRig.model_validate({**RIG, "behaviorBoard": {"main": board}})
    wiring = rig.behavior_board["main"].wiring
    assert wiring.di_port0.device == "Photodiode" and wiring.do1.side.value == "left"
    assert wiring.do2 is None, "a line left out is unknown"
    dumped = rig.model_dump(by_alias=True, exclude_none=True, mode="json")["behaviorBoard"]["main"]["wiring"]
    assert dumped == board["wiring"], "written back under the board's own channel names"


def test_wiring_optional_and_side_checked():
    assert BehaviourBoxRig.model_validate(RIG).behavior_board["main"].wiring is None
    with pytest.raises(ValidationError):
        BehaviourBoxRig.model_validate({**RIG, "behaviorBoard": {"main": {
            "portName": "COM3", "usbSerial": "X", "wiring": {"DO1": {"device": "RewardValve", "side": "middle"}}}}})
    with pytest.raises(ValidationError):
        BehaviourBoxRig.model_validate({**RIG, "behaviorBoard": {"main": {
            "portName": "COM3", "usbSerial": "X", "wiring": {"DO1": {"side": "left"}}}}})


def test_stepper_wiring_by_the_driver_s_names():
    from open_rigs.rigs.behaviour_box import HarpStepperDriver

    entry = {"portName": "COM3", "usbSerial": "AQ03WRBF", "wiring": {
        "Motor0": {"device": "BaseTransverse"},
        "Motor1": {"device": "Radial", "side": "left"},
        "Input0": {"device": "HomeSwitch", "note": "base transverse"}}}
    driver = HarpStepperDriver.model_validate(entry)
    assert driver.wiring.motor1.side.value == "left" and driver.wiring.motor2 is None
    assert driver.model_dump(by_alias=True, exclude_none=True, mode="json")["wiring"] == entry["wiring"]
    assert HarpStepperDriver.model_validate({"portName": "COM3"}).wiring is None
    with pytest.raises(ValidationError):
        HarpStepperDriver.model_validate({**entry, "wiring": {"Motor1": {"device": "Radial", "side": "middle"}}})


def test_wiring_schema_lists_every_channel():
    schema = BehaviourBoxRig.model_json_schema(by_alias=True, mode="serialization")
    channels = list(schema["$defs"]["BoardWiring"]["properties"])
    assert channels[:4] == ["DIPort0", "DIPort1", "DIPort2", "DI3"] and len(channels) == 23
    assert {"DO0", "DO3", "AnalogInput1", "Rgb1"} <= set(channels)
