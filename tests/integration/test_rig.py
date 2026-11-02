from open_rigs.core.rig import Rig
from open_rigs.devices.arduino import LedController, LedDriver
from open_rigs.devices.behavior_board import BehaviorBoard
from open_rigs.devices.lick_spout_stage import LickSpoutStageDriver, SpoutRigPosition
from open_rigs.devices.lickety_split import LicketySplit
from open_rigs.vision.displays import Screen


class DummyRig(Rig):
    led_driver: LedDriver
    behavior_board: BehaviorBoard
    lick_spout: LickSpoutStageDriver
    lickety_split: LicketySplit
    screen: Screen


def test_rig_validate():
    rig = DummyRig(
        root_path="data",
        led_driver=LedDriver(
            port_name="COM5", sampling_interval=10, led_controller=LedController(digital_out_pin=8)
        ),
        behavior_board=BehaviorBoard(port_name="COM3"),
        lick_spout=LickSpoutStageDriver(port_name="COM2", set_position=SpoutRigPosition()),
        lickety_split=LicketySplit(port_name="COM1"),
        screen=Screen(),
    )
    assert rig.root_path == "data"
    assert rig.rig_id is None


def test_rig_id():
    rig = Rig.model_validate({"rootPath": "data", "rigId": "B1"})
    assert rig.rig_id == "B1"
    assert rig.model_dump(by_alias=True)["rigId"] == "B1"
