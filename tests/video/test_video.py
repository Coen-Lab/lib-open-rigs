import pytest
from pydantic import Field, ValidationError
from typing import Dict, List
from open_rigs.video import Camera, ArducamOV9180, SpinnakerCamera
from open_rigs.core.rig import Rig
from open_rigs.core import DiscriminatorTypeMixin
from swc.aeon.schema import BaseSchema


class CameraRig(Rig):
    cameras: Dict[str, Camera]


class ExcludedDevice(DiscriminatorTypeMixin, BaseSchema):
    test_parameter: int = Field(default=0)


@pytest.mark.parametrize(
    ("klass", "parameter", "expected"),
    [
        (
            CameraRig,
            {"spinnaker": SpinnakerCamera(), "arducam": ArducamOV9180()},
            ["spinnaker", "SpinnakerCamera"],
        ),
        (
            CameraRig,
            {"spinnaker": SpinnakerCamera(), "arducam": ArducamOV9180()},
            ["arducam", "ArducamOV9180"],
        ),
    ],
)
def test_camera_discriminator(klass: type[CameraRig], parameter: Dict[str, Camera], expected: List[str]):
    inst = klass(root_path="", cameras=parameter)
    json_str = inst.model_dump_json()
    deserialized = klass.model_validate_json(json_str)
    assert deserialized.cameras[expected[0]].root.discriminator_type == expected[1]


@pytest.mark.parametrize(
    ("klass", "parameter", "expected"),
    [
        (
            CameraRig,
            {"spinnaker": SpinnakerCamera(), "arducam": ArducamOV9180(), "excluded": ExcludedDevice()},
            ValidationError,
        )
    ],
)
def test_camera_exclusion(klass: type[CameraRig], parameter: Dict[str, Camera], expected: type):
    with pytest.raises(expected):
        klass(root_path="", cameras=parameter)


def test_spinnaker_pixel_format_round_trip():
    inst = CameraRig(root_path="", cameras={"spinnaker": SpinnakerCamera.model_validate({"pixel_format": "Mono16"})})
    deserialized = CameraRig.model_validate_json(inst.model_dump_json())
    assert SpinnakerCamera().pixel_format == "Mono8"
    assert deserialized.cameras["spinnaker"].root.pixel_format == "Mono16"
