"""The lab's Harp boards: open_rigs' own, with their wiring, a liveView: block for StreamDevices
(which reads rig.yml itself) and, on the Behavior board, its USB serial required. They keep
open_rigs' names, so the generated C# classes keep theirs."""
from typing import Optional

from pydantic import Field

from open_rigs.components.live_view import LiveView
from open_rigs.components.wiring import BoardWiring, StepperWiring
from open_rigs.devices.behavior_board import BehaviorBoard as _BehaviorBoard
from open_rigs.devices.harp import HarpStepperDriver as _HarpStepperDriver


class HarpStepperDriver(_HarpStepperDriver):
    __doc__ = _HarpStepperDriver.__doc__
    wiring: Optional[StepperWiring] = Field(
        default=None,
        description="What each motor and input is connected to. Not checked at Start.",
    )


class BehaviorBoard(_BehaviorBoard):
    __doc__ = _BehaviorBoard.__doc__
    # Required here, optional on open_rigs' HarpDevice: OpenRigs.HarpDevices checks every board by it.
    usb_serial: str = Field(  # pyright: ignore[reportIncompatibleVariableOverride]
        description="The board's USB serial number, its FTDI chip's (AB0M7PLB): which board this is. OpenRigs.HarpDevices stops the workflow at Start if the board on portName has another. The rig's hardware scan lists each port's (serialPorts, usbSerial)."
    )
    live_view: Optional[LiveView] = Field(default=None, description="Live view settings for this board (OpenRigs.LiveView).")
    wiring: Optional[BoardWiring] = Field(
        default=None,
        description="What is plugged into each line. Reward valves and the Zapit trigger are read from here; nothing else is checked at Start against the workflow.",
    )
