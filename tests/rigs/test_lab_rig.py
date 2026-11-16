import copy

import pytest
from pydantic import ValidationError

from open_rigs.rigs.lab_rig import LabRig

HARP = {"portName": "COM1", "usbSerial": "X"}
STAGE = {"baseTransverse": 0, "leftRadial": 0, "rightRadial": 0, "leftElevation": 0, "rightElevation": 0}
CAMERA_LINE = {"DO0": {"device": "CameraTrigger", "targets": ["Camera-Face", "Camera-Pupil", "Camera-Body"]}}

RIG = {
    "rootPath": "C:\\Data",
    "behaviorBoard": {"main": {
        **HARP,
        "pulseController": {"outputPulseEnable": "DO1, DO2", "outputs": {
            "DO1": {"discriminatorType": "CalibratedPulse", "artefact": "calibration/valve-DO1.json"},
            "DO2": {"discriminatorType": "CalibratedPulse", "artefact": "calibration/valve-DO2.json"},
            "DO3": {"discriminatorType": "FixedPulse", "pulseWidthMs": 0},
        }},
        "wiring": {
            **CAMERA_LINE,
            "DO1": {"device": "RewardValve", "side": "left"},
            "DO2": {"device": "RewardValve", "side": "right"},
            "DO3": {"device": "ZapitTrigger"},
        },
    }},
    "timestampGeneratorGen3": {"main": HARP},
    "stepperDriver": {"a": HARP, "b": HARP},
    "licketySplit": {"left": HARP, "right": HARP},
    "camera": {"face": {"serialNumber": "1"}, "pupil": {"serialNumber": "2"}, "body": {"serialNumber": "3"}},
    "projectionCalibration": {"meshMapPath": "calibration/MeshMap.csv", "gammaLutPath": "calibration/gammalut.bmp"},
    "speakerArray": {
        "device": {"deviceName": "Speakers"},
        "speakers": {"centre": {"position": {"azimuth": 0, "elevation": 15}, "channel": 2, "filter": "calibration/speaker-centre.json"}},
        "penaltySoundChannel": 2,
    },
    "lickSpoutDefaultPosition": STAGE,
    "zapit": {},
}


def rig_with(**wiring):
    rig = copy.deepcopy(RIG)
    rig["behaviorBoard"]["main"]["wiring"] = {**CAMERA_LINE, **wiring}
    return rig


def test_template_validates():
    rig = LabRig.model_validate(RIG)
    assert rig.speaker_array.penalty_sound_channel == 2


def test_the_background_noise_is_a_rig_file_the_speaker_array_names():
    # The lab's one noise (16 Nov 2026): RigConfigs\shared beside the rig's folder, listed with the rig's other files.
    from open_rigs.core.artefacts import rig_artefacts

    rig = copy.deepcopy(RIG)
    rig["speakerArray"]["backgroundNoise"] = "../shared/sounds/background_noise.wav"
    found = rig_artefacts(LabRig.model_validate(rig))
    assert found["speakerArray.backgroundNoise"] == "../shared/sounds/background_noise.wav"
    assert LabRig.model_validate(RIG).speaker_array.background_noise is None


@pytest.mark.parametrize("key, path", [("meshMapPath", "C:\\RigConfigs\\RIG\\calibration\\MeshMap.csv"),
                                       ("gammaLutPath", "/calibration/gammalut.bmp")])
def test_projector_files_are_relative_to_rig_yml(key, path):
    # ProjectorDisplay resolves them against rig.yml's folder and refuses a full path at Start.
    from open_rigs.core.artefacts import rig_artefacts

    found = rig_artefacts(LabRig.model_validate(RIG))
    assert found["projectionCalibration.meshMapPath"] == "calibration/MeshMap.csv"
    assert found["projectionCalibration.gammaLutPath"] == "calibration/gammalut.bmp"
    rig = copy.deepcopy(RIG)
    rig["projectionCalibration"][key] = path
    name = path.replace("\\", "/").rsplit("/", 1)[1]
    with pytest.raises(ValidationError, match=f"projectionCalibration.{key} is a full path") as refused:
        LabRig.model_validate(rig)
    assert f"`{key}: calibration/{name}`" in str(refused.value)


def test_old_keys_gone():
    # The generated C# refuses a key its classes lack, so a rig.yml still holding these stops at Start.
    keys = set(LabRig.model_json_schema(by_alias=True)["properties"])
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
    with pytest.raises(ValidationError, match=f"rig.yml has {keys}, which no lab workflow uses") as refused:
        LabRig.model_validate(rig)
    assert "(lab rig model, open_rigs.rigs.lab_rig)" in str(refused.value)


def test_people_only_wiring_keys_kept():
    rig = copy.deepcopy(RIG)
    rig["behaviorBoard"]["main"]["wiring"]["DO3"] = {"device": "ZapitTrigger", "note": "also the masking light", "part": "BNC"}
    LabRig.model_validate(rig)


def test_every_part_optional():
    # Each workflow takes the parts it drives: a rig.yml with none of them, or a box's two, is a rig.
    rig = LabRig.model_validate({"rootPath": "C:/Data"})
    assert (rig.behavior_board, rig.camera, rig.speaker_array, rig.zapit, rig.projection_calibration) == ({}, {}, None, None, None)
    trigger = {"wiring": {"DO0": {"device": "CameraTrigger", "targets": ["Camera-Overhead"]}}}
    box = LabRig.model_validate({"rootPath": "C:/Data", "behaviorBoard": {"main": {**HARP, **trigger}}, "camera": {"overhead": {"serialNumber": "1"}}})
    assert list(box.camera) == ["overhead"] and list(box.behavior_board) == ["main"]



def test_a_rig_built_by_field_name():
    # A workflow's rig template is built in Python, its parts named by field.
    from open_rigs.components.harp_boards import BehaviorBoard
    from open_rigs.components.video import VideoEncoder

    rig = LabRig(root_path="C:/Data", rig_id="S1", behavior_board={"main": BehaviorBoard(port_name="COM3", usb_serial="X")},
                 video_encoder=VideoEncoder(codec="h264_qsv"))
    assert rig.video_encoder is not None and rig.video_encoder.codec == "h264_qsv" and rig.rig_id == "S1"


def test_one_valve_is_enough():
    rig = copy.deepcopy(RIG)
    del rig["zapit"]
    rig["behaviorBoard"]["main"]["wiring"] = {**CAMERA_LINE, "DO1": {"device": "RewardValve", "side": "left"}}
    LabRig.model_validate(rig)


def test_reward_valves_on_two_boards_are_refused():
    # Audit B2: every valve fires through one board's TriggerCalibratedPulse, so the second board's valve never opened.
    rig = copy.deepcopy(RIG)
    main = rig["behaviorBoard"]["main"]
    second = copy.deepcopy(main)
    del main["wiring"]["DO2"]
    second.update(portName="COM9", wiring={"DO2": {"device": "RewardValve", "side": "right"}})
    rig["behaviorBoard"]["second"] = second
    with pytest.raises(ValidationError, match="behaviorBoard main and second each wire a RewardValve, and a rig's reward valves are on one board"):
        LabRig.model_validate(rig)


def test_zapit_needs_its_trigger_line():
    rig = copy.deepcopy(RIG)
    rig["behaviorBoard"]["main"]["wiring"] = {**CAMERA_LINE, "DO1": {"device": "RewardValve", "side": "left"}}
    with pytest.raises(ValidationError, match="rig.yml has zapit, so a board's wiring needs its ZapitTrigger line") as refused:
        LabRig.model_validate(rig)
    assert "`DO3: { device: ZapitTrigger }`" in str(refused.value)


def test_one_zapit_trigger_per_rig():
    rig = copy.deepcopy(RIG)
    rig["behaviorBoard"]["second"] = copy.deepcopy(rig["behaviorBoard"]["main"])
    with pytest.raises(ValidationError, match="behaviorBoard main and second each wire a ZapitTrigger"):
        LabRig.model_validate(rig)


@pytest.mark.parametrize(
    "wiring, message, example",
    [
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO2": {"device": "RewardValve", "side": "left"},
          "DO3": {"device": "ZapitTrigger"}}, "has 2 left RewardValves", "`DO1: { device: RewardValve, side: left }`"),
        ({"DO1": {"device": "RewardValve"}, "DO2": {"device": "RewardValve", "side": "right"},
          "DO3": {"device": "ZapitTrigger"}}, "DO1 has no side", "`DO1: { device: RewardValve, side: left }`"),
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO2": {"device": "RewardValve", "side": "right"},
          "DO3": {"device": "ZapitTrigger"}, "DO0": {"device": "ZapitTrigger"}}, "has 2 ZapitTriggers",
         "`DO3: { device: ZapitTrigger }`"),
        ({"DO1": {"device": "RewardValve", "side": "left"}, "DO3": {"device": "RewardValve", "side": "right"},
          "DO2": {"device": "ZapitTrigger"}}, "RewardValve DO3 needs a CalibratedPulse",
         "`DO3: { discriminatorType: CalibratedPulse, artefact: calibration/valve-DO3.json }`"),
    ],
)
def test_wiring_refused(wiring, message, example):
    with pytest.raises(ValidationError, match=message) as refused:
        LabRig.model_validate(rig_with(**wiring))
    assert "(lab rig model, open_rigs.rigs.lab_rig)" in str(refused.value)
    assert example in str(refused.value)


@pytest.mark.parametrize("pulsed, kept", [("DO1, DO2, DO3", "DO1, DO2"), (None, "DO1, DO2"), ("DO3", "None")])
def test_zapit_line_not_pulsed(pulsed, kept):
    # A pulse-enabled line ends itself after its PulseDOx width; the Zapit line is held until the workflow clears it.
    rig = copy.deepcopy(RIG)
    controller = rig["behaviorBoard"]["main"]["pulseController"]
    if pulsed is None:
        del controller["outputPulseEnable"]  # the default pulse-enables DO1 to DO3
    else:
        controller["outputPulseEnable"] = pulsed
    with pytest.raises(ValidationError, match="ZapitTrigger DO3 is in pulseController.outputPulseEnable") as refused:
        LabRig.model_validate(rig)
    assert "(lab rig model, open_rigs.rigs.lab_rig)" in str(refused.value)
    assert f'`outputPulseEnable: "{kept}"`' in str(refused.value)


def test_penalty_sound_channel_required():
    rig = copy.deepcopy(RIG)
    del rig["speakerArray"]["penaltySoundChannel"]
    with pytest.raises(ValidationError, match="penaltySoundChannel"):
        LabRig.model_validate(rig)


def test_every_speaker_needs_a_filter():
    rig = copy.deepcopy(RIG)
    rig["speakerArray"]["speakers"]["left"] = {"position": {"azimuth": -90, "elevation": 15}, "channel": 0}
    with pytest.raises(ValidationError, match=r"speaker left has no filter.*calibration/speaker-left.json"):
        LabRig.model_validate(rig)


def test_speaker_channels_are_refused_as_the_workflow_refuses_them_at_start():
    # OpenRigs.Audio SpeakerSet.Load stopped the workflow at Start; the model said nothing.
    rig = copy.deepcopy(RIG)
    array = rig["speakerArray"]
    array["device"]["channelCount"] = 4
    array["speakers"]["left"] = {"position": {"azimuth": -90, "elevation": 15}, "channel": 2, "filter": "calibration/speaker-left.json"}
    array["speakers"]["right"] = {"position": {"azimuth": 90, "elevation": 15}, "channel": 4, "filter": "calibration/speaker-right.json"}
    array["syncChannel"] = 4
    array["penaltySoundChannel"] = 3
    with pytest.raises(ValidationError) as refused:
        LabRig.model_validate(rig)
    assert ("speaker 'right' channel 4 is not one of the audio device's 4 channels (0 to 3); speakers centre, left share channel 2: "
            "each speaker needs its own; syncChannel 4 is not one of the audio device's 4 channels (0 to 3); syncChannel 4 has speaker "
            "right on it: the sync copy needs a channel of its own; penaltySoundChannel 3 has no speaker on it") in str(refused.value)


def test_the_sync_channel_has_no_speaker_on_it():
    # OpenRigs.Audio SpeakerSet.AddSyncCopy stopped the workflow at the first stimulus.
    rig = copy.deepcopy(RIG)
    rig["speakerArray"]["syncChannel"] = rig["speakerArray"]["speakers"]["centre"]["channel"]
    with pytest.raises(ValidationError, match="has speaker centre on it: the sync copy needs a channel of its own"):
        LabRig.model_validate(rig)


def test_video_encoder_optional():
    assert LabRig.model_validate(RIG).video_encoder is None
    assert LabRig.model_validate({**RIG, "videoEncoder": {"codec": "h264_nvenc"}}).video_encoder.codec == "h264_nvenc"
    with pytest.raises(ValidationError, match="ffmpeg"):
        LabRig.model_validate({**RIG, "videoEncoder": {"codec": "h264_nvenc", "ffmpeg": "x"}})


def test_the_lab_rig_template_validates_and_fits_the_committed_schema():
    # Every lab workflow's rig.yml starts from this file and is read by OpenRigs.Devices.Rigs, generated
    # from schemas/lab_rig.json: regenerate both with `uv run regenerate-lab-rig`.
    import json
    from importlib import resources

    import yaml

    from open_rigs._generators.lab_rig import build_schema

    package = resources.files("open_rigs")
    template = yaml.safe_load((package / "rigs" / "rig-template.yml").read_text(encoding="utf-8"))
    rig = LabRig.model_validate(template)
    assert rig.behavior_board["main"].wiring.do3.device == "ZapitTrigger"
    committed = json.loads((package / "schemas" / "lab_rig.json").read_text(encoding="utf-8"))
    assert committed == json.loads(json.dumps(build_schema()))


def test_the_mixer_boot_delay_is_in_seconds():
    defs = LabRig.model_json_schema(by_alias=True)["$defs"]
    assert "bootDelaySeconds" in defs["AudioDevice"]["properties"]


def test_the_reward_valve_lines_are_wiring_s():
    assert LabRig.model_validate(RIG).reward_valve_lines() == {1, 2}
    assert LabRig.model_validate(rig_with(DO2={"device": "RewardValve", "side": "right"}, DO3={"device": "ZapitTrigger"})).reward_valve_lines() == {2}
