"""The lab's rig parts (open_rigs.components), read through the lab rig model."""
import pytest
from pydantic import ValidationError

from open_rigs.components.harp_boards import HarpStepperDriver
from open_rigs.components.video import VideoEncoder
from open_rigs.rigs.lab_rig import LabRig

RIG = {
    "rootPath": "C:\\Data\\B1",
    "rigId": "B1",
    "behaviorBoard": {"main": {"portName": "COM3", "usbSerial": "AB0M7PLB", "wiring": {"DO0": {"device": "CameraTrigger", "targets": ["Camera-Overhead"]}}}},
    "camera": {"overhead": {"serialNumber": "12345"}},
}


def test_named_entries():
    rig = LabRig.model_validate(RIG)
    assert rig.behavior_board["main"].usb_serial == "AB0M7PLB"
    assert rig.camera["overhead"].live_view is None


def test_pixel_format_kept_by_the_camera_unless_set():
    assert LabRig.model_validate(RIG).camera["overhead"].pixel_format is None
    rig = LabRig.model_validate({**RIG, "camera": {"overhead": {"serialNumber": "12345", "pixelFormat": "Mono8"}}})
    assert rig.camera["overhead"].pixel_format == "Mono8"
    # LogVideoFfmpeg writes 8-bit frames only.
    with pytest.raises(ValidationError):
        LabRig.model_validate({**RIG, "camera": {"overhead": {"serialNumber": "12345", "pixelFormat": "Mono16"}}})



def test_each_camera_on_exactly_one_trigger_line():
    def rig(wiring):
        return {**RIG, "behaviorBoard": {"main": {"portName": "COM3", "usbSerial": "X", "wiring": wiring}}}

    face = {"DO0": {"device": "CameraTrigger", "targets": ["Camera-Face"]}}
    off_trigger = {"DO2": {"device": "CameraTrigger", "targets": ["Camera-Overhead"]}}
    twice = {line: {"device": "CameraTrigger", "targets": ["Camera-Overhead"]} for line in ("DO0", "DO1")}
    for wiring in ({}, face, off_trigger, twice):
        with pytest.raises(ValidationError, match="camera overhead is on"):
            LabRig.model_validate(rig(wiring))


def test_usb_serial_required():
    with pytest.raises(ValidationError):
        LabRig.model_validate({**RIG, "behaviorBoard": {"main": {"portName": "COM3"}}})


def test_wiring_by_channel_name():
    board = {
        "portName": "COM6",
        "usbSerial": "BG01WA0E",
        "pulseController": {"outputs": {"DO1": {"discriminatorType": "CalibratedPulse", "artefact": "calibration/valve-DO1.json"},
                                         "DO2": {"discriminatorType": "FixedPulse", "pulseWidthMs": 0},
                                         "DO3": {"discriminatorType": "FixedPulse", "pulseWidthMs": 0}}},
        "wiring": {
            "DIPort0": {"device": "Photodiode", "note": "sync square"},
            "DO0": {"device": "CameraTrigger", "targets": ["Camera-Overhead"]},
            "DO1": {"device": "RewardValve", "name": "left", "part": "?"},
        },
    }
    rig = LabRig.model_validate({**RIG, "behaviorBoard": {"main": board}})
    wiring = rig.behavior_board["main"].wiring
    assert wiring.di_port0.device == "Photodiode" and wiring.do1.name == "left"
    assert wiring.do2 is None, "a line left out is unknown"
    dumped = rig.model_dump(by_alias=True, exclude_none=True, mode="json")["behaviorBoard"]["main"]["wiring"]
    assert dumped == board["wiring"], "written back under the board's own channel names"


def test_wiring_optional_and_name_checked():
    no_camera = {key: value for key, value in RIG.items() if key != "camera"}
    plain = {**no_camera, "behaviorBoard": {"main": {"portName": "COM3", "usbSerial": "X"}}}
    assert LabRig.model_validate(plain).behavior_board["main"].wiring is None
    with pytest.raises(ValidationError):
        LabRig.model_validate({**RIG, "behaviorBoard": {"main": {
            "portName": "COM3", "usbSerial": "X", "wiring": {"DO1": {"device": "RewardValve", "name": "Left Valve"}}}}})
    with pytest.raises(ValidationError):
        LabRig.model_validate({**RIG, "behaviorBoard": {"main": {
            "portName": "COM3", "usbSerial": "X", "wiring": {"DO1": {"side": "left"}}}}})


def test_stepper_wiring_by_the_driver_s_names():
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
    schema = LabRig.model_json_schema(by_alias=True, mode="serialization")
    channels = list(schema["$defs"]["BoardWiring"]["properties"])
    assert channels[:4] == ["DIPort0", "DIPort1", "DIPort2", "DI3"] and len(channels) == 23
    assert {"DO0", "DO3", "AnalogInput1", "Rgb1"} <= set(channels)


def test_video_encoder_libx264_and_path_unless_set():
    assert LabRig.model_validate(RIG).video_encoder is None
    rig = LabRig.model_validate({**RIG, "videoEncoder": {"codec": "h264_qsv", "ffmpegPath": r"D:\ffmpeg\ffmpeg.exe"}})
    assert (rig.video_encoder.codec, rig.video_encoder.ffmpeg_path) == ("h264_qsv", r"D:\ffmpeg\ffmpeg.exe")
    assert LabRig.model_validate({**RIG, "videoEncoder": {}}).video_encoder.codec == "libx264"
    # codec: null in rig.yml is libx264, as OpenRigs.Video's VideoEncoder.Read takes it.
    assert LabRig.model_validate({**RIG, "videoEncoder": {"codec": None}}).video_encoder.codec == "libx264"
    # OpenRigs.Video's FfmpegVideoWriter knows these three alone.
    with pytest.raises(ValidationError):
        LabRig.model_validate({**RIG, "videoEncoder": {"codec": "hevc_nvenc"}})


def test_video_encoder_codec_is_a_string_in_the_schema_so_rig_yml_can_say_h264_nvenc():
    # An enum here generates a C# enum, and the workflow's YAML reader then refuses h264_nvenc at Start.
    codec = VideoEncoder.model_json_schema()["properties"]["codec"]
    assert codec["type"] == "string" and "enum" not in codec
