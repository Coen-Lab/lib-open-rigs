from typing import Dict, List

from pydantic import BaseModel, Field

from open_rigs.components.audio import SpeakerArray, SpeakerFilter
from open_rigs.core.artefacts import SCHEMA_TAG, ArtefactPath, rig_artefacts
from open_rigs.core.rig import Rig
from open_rigs.devices.behavior_board import BehaviorBoard
from open_rigs.vision.projection import MeshMap, ProjectionCorrection


class Sensor(BaseModel):
    curve: ArtefactPath = Field(description="Required reference.")
    filter: ArtefactPath | None = Field(default=None, description="Optional reference.")
    label: str = Field(default="sensor", description="Not a reference.")


class Bench(BaseModel):
    main: Sensor
    spares: List[Sensor] = Field(default=[])
    named: Dict[str, Sensor] = Field(default={})


def test_bare_rig_references_nothing():
    assert rig_artefacts(Rig(root_path="C:\\Data")) == {}


def test_required_and_optional_references_are_found_unset_ones_skipped():
    bench = Bench(main=Sensor(curve="calibration/main.json"))
    assert rig_artefacts(bench) == {"main.curve": "calibration/main.json"}


def test_lists_and_dictionaries_are_walked():
    bench = Bench(
        main=Sensor(curve="a.json", filter="b.json"),
        spares=[Sensor(curve="c.json")],
        named={"left": Sensor(curve="d.json")},
    )
    assert rig_artefacts(bench) == {
        "main.curve": "a.json",
        "main.filter": "b.json",
        "spares[0].curve": "c.json",
        "named.left.curve": "d.json",
    }


def test_plain_strings_are_not_references():
    assert "main.label" not in rig_artefacts(Bench(main=Sensor(curve="a.json")))


def test_schema_tags_references_and_keeps_them_strings():
    properties = Sensor.model_json_schema()["properties"]
    assert properties["curve"]["type"] == "string"
    assert properties["curve"][SCHEMA_TAG] is True
    assert "label" in properties and SCHEMA_TAG not in properties["label"]


class DomeRig(Rig):
    """A rig composing several devices, each carrying its own calibration references."""

    behavior_board: BehaviorBoard
    speaker_array: SpeakerArray
    projection: ProjectionCorrection


def dome_rig() -> DomeRig:
    # Validated from camelCase dicts, the way rig YAML arrives.
    return DomeRig.model_validate(
        {
            "rootPath": "C:/Data",
            "behaviorBoard": {
                "portName": "COM3",
                "pulseController": {
                    "outputs": {
                        "DO1": {"discriminatorType": "CalibratedPulse", "artefact": "calibration/valve-DO1.json"},
                        "DO2": {"discriminatorType": "FixedPulse", "pulseWidthMs": 0},
                        "DO3": {"discriminatorType": "FixedPulse", "pulseWidthMs": 20},
                    }
                },
            },
            "speakerArray": {
                "device": {"deviceName": "Speakers"},
                "speakers": {
                    "left": {"position": {"azimuth": -90, "elevation": 15}, "channel": 0, "filter": "calibration/speaker-left.json"},
                    "right": {"position": {"azimuth": 90, "elevation": 15}, "channel": 1},
                },
            },
            "projection": {"meshMap": "calibration/mesh.json"},
        }
    )


def test_composed_rig_lists_each_device_reference():
    assert rig_artefacts(dome_rig()) == {
        "behaviorBoard.pulseController.outputs.DO1.artefact": "calibration/valve-DO1.json",
        "speakerArray.speakers.left.filter": "calibration/speaker-left.json",
        "projection.meshMap": "calibration/mesh.json",
    }


def test_device_artefacts_round_trip_through_json():
    provenance = {"calibratedAt": "2026-10-01T10:00:00Z", "calibratedBy": "Experimenter", "machineName": "RIG-NAME"}
    mesh = MeshMap.model_validate({**provenance, "azimuthResolution": 30, "elevationResolution": 30, "meshPath": "calibration/mesh-map.csv"})
    speaker = SpeakerFilter.model_validate({**provenance, "taps": [0.5, 0.25], "sampleRate": 48000})
    assert MeshMap.model_validate_json(mesh.model_dump_json(by_alias=True)).model_dump() == mesh.model_dump()
    assert SpeakerFilter.model_validate_json(speaker.model_dump_json(by_alias=True)).model_dump() == speaker.model_dump()
