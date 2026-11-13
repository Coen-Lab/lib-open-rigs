import copy

import pytest
from pydantic import ValidationError

from open_rigs.rigs.dome import DomeRig

HARP = {"portName": "COM1", "usbSerial": "X"}
STAGE = {"baseTransverse": 0, "leftRadial": 0, "rightRadial": 0, "leftElevation": 0, "rightElevation": 0}
RIG = {
    "rootPath": "C:\\Data",
    "behaviorBoard": {"main": {
        **HARP,
        "pulseController": {"outputs": {
            "DO1": {"discriminatorType": "CalibratedPulse", "artefact": "calibration/valve-DO1.json"},
            "DO2": {"discriminatorType": "CalibratedPulse", "artefact": "calibration/valve-DO2.json"},
            "DO3": {"discriminatorType": "FixedPulse", "pulseWidthMs": 0},
        }},
        "wiring": {
            "DO1": {"device": "RewardValve", "side": "left"},
            "DO2": {"device": "RewardValve", "side": "right"},
            "DO3": {"device": "ZapitTrigger"},
        },
    }},
    "timestampGeneratorGen3": {"main": HARP},
    "stepperDriver": {"a": HARP, "b": HARP},
    "licketySplit": {"left": HARP, "right": HARP},
    "camera": {"face": {"serialNumber": "1"}, "pupil": {"serialNumber": "2"}, "body": {"serialNumber": "3"}},
    "projectionCalibration": {"meshMapPath": "m.csv", "gammaLutPath": "g.bmp"},
    "speakerArray": {
        "device": {"deviceName": "Speakers"},
        "speakers": {"centre": {"position": {"azimuth": 0, "elevation": 15}, "channel": 2}},
        "errorSoundChannel": 2,
    },
    "lickSpoutDefaultPosition": STAGE,
    "zapit": {},
}


def rig_with(**wiring):
    rig = copy.deepcopy(RIG)
    rig["behaviorBoard"]["main"]["wiring"] = wiring
    return rig


def test_template_validates():
    rig = DomeRig.model_validate(RIG)
    assert rig.speaker_array.error_sound_channel == 2
    assert "triggerLine" not in DomeRig.model_json_schema(by_alias=True)["$defs"]["ZapitServer"]["properties"]


def test_old_keys_gone():
    # The generated C# refuses a key its classes lack, so a rig.yml still holding these stops at Start.
    keys = set(DomeRig.model_json_schema(by_alias=True)["properties"])
    assert not keys & {"rewardValves", "interPulseInterval", "version"}


@pytest.mark.parametrize(
    "edit, keys",
    [
        (lambda rig: rig.update(rewardValves={"left": 1}, interPulseInterval=0.1), "interPulseInterval, rewardValves"),
        (lambda rig: rig["zapit"].update(triggerLine="DO3"), "zapit.triggerLine"),
        (lambda rig: rig["behaviorBoard"]["main"]["wiring"]["DO1"].update(valve="left"),
         "behaviorBoard.main.wiring.DO1.valve"),
        (lambda rig: rig["camera"]["face"].update(exposure=1), "camera.face.exposure"),
    ],
)
def test_unknown_keys_refused(edit, keys):
    # The generated C# refuses a key its classes lack, so a rig.yml still holding one stops at Start.
    rig = copy.deepcopy(RIG)
    edit(rig)
    with pytest.raises(ValidationError, match=f"rig.yml has {keys}, which a dome does not use") as refused:
        DomeRig.model_validate(rig)
    assert "(dome rig model, open_rigs.rigs.dome)" in str(refused.value)


def test_people_only_wiring_keys_kept():
    rig = copy.deepcopy(RIG)
    rig["behaviorBoard"]["main"]["wiring"]["DO3"] = {"device": "ZapitTrigger", "note": "also the masking light", "part": "BNC"}
    DomeRig.model_validate(rig)


def test_wiring_required():
    rig = copy.deepcopy(RIG)
    del rig["behaviorBoard"]["main"]["wiring"]
    with pytest.raises(ValidationError, match="wiring"):
        DomeRig.model_validate(rig)


@pytest.mark.parametrize(
    "wiring, message, example",
    [
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO3": {"device": "ZapitTrigger"}}, "one right RewardValve, has 0",
         "`DO2: { device: RewardValve, side: right }`"),
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO2": {"device": "RewardValve", "side": "left"},
          "DO3": {"device": "ZapitTrigger"}}, "one left RewardValve, has 2", "`DO1: { device: RewardValve, side: left }`"),
        ({"DO1": {"device": "RewardValve"}, "DO2": {"device": "RewardValve", "side": "right"},
          "DO3": {"device": "ZapitTrigger"}}, "DO1 has no side", "`DO1: { device: RewardValve, side: left }`"),
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO2": {"device": "RewardValve", "side": "right"}},
         "one ZapitTrigger, has 0", "`DO3: { device: ZapitTrigger }`"),
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO2": {"device": "RewardValve", "side": "right"},
          "DO3": {"device": "ZapitTrigger"}, "DO0": {"device": "ZapitTrigger"}}, "one ZapitTrigger, has 2",
         "`DO3: { device: ZapitTrigger }`"),
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO3": {"device": "RewardValve", "side": "right"},
          "DO2": {"device": "ZapitTrigger"}}, "RewardValve DO3 needs a CalibratedPulse",
         "`DO3: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO3.json }`"),
    ],
)
def test_wiring_refused(wiring, message, example):
    with pytest.raises(ValidationError, match=message) as refused:
        DomeRig.model_validate(rig_with(**wiring))
    assert "(dome rig model, open_rigs.rigs.dome)" in str(refused.value)
    assert example in str(refused.value)


def test_error_sound_channel_required():
    rig = copy.deepcopy(RIG)
    del rig["speakerArray"]["errorSoundChannel"]
    with pytest.raises(ValidationError, match="errorSoundChannel"):
        DomeRig.model_validate(rig)


@pytest.mark.parametrize("cameras", [1, 4])
def test_exactly_three_cameras(cameras):
    rig = copy.deepcopy(RIG)
    rig["camera"] = {f"c{n}": {"serialNumber": str(n)} for n in range(cameras)}
    with pytest.raises(ValidationError, match=rf"camera lists {cameras} entries; a dome has exactly 3 \(dome rig model"):
        DomeRig.model_validate(rig)
