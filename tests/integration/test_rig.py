from open_rigs.core.rig import Rig
from open_rigs.devices.behavior_board import BehaviorBoard
from open_rigs.devices.lickety_split import LicketySplit
from open_rigs.vision.displays import Screen


class DummyRig(Rig):
    behavior_board: BehaviorBoard
    lickety_split: LicketySplit
    screen: Screen


def test_rig_validate():
    rig = DummyRig(
        root_path="data",
        behavior_board=BehaviorBoard(port_name="COM3"),
        lickety_split=LicketySplit(port_name="COM1"),
        screen=Screen(),
    )
    assert rig.root_path == "data"
    assert rig.rig_id is None


def test_rig_id():
    rig = Rig.model_validate({"rootPath": "data", "rigId": "B1"})
    assert rig.rig_id == "B1"
    assert rig.model_dump(by_alias=True)["rigId"] == "B1"


def test_no_model_nothing_drives_is_kept():
    # Round 3 LL5: the Arduino LED driver and the Arduino lick spout stage (LickSpoutStageDriver, SpoutRigPosition,
    # StageAxisMapping) had no workflow, include or rig.yml key; they went, with their example and generated C#.
    import open_rigs.devices as devices
    from pathlib import Path

    gone = ("ArduinoDevice", "LedDriver", "LickSpoutStageDriver", "SpoutRigPosition", "StageAxisMapping")
    assert not [name for name in gone if hasattr(devices, name)]
    generated = (Path(__file__).parents[2] / "src" / "OpenRigs.Core" / "DataTypes.Generated.cs").read_text(encoding="utf-8")
    assert "SpoutRigPosition" not in generated
