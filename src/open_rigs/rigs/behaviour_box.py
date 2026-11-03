from typing import Dict, Optional

from pydantic import Field
from swc.aeon.schema import BaseSchema

from open_rigs.core.rig import Rig
from open_rigs.devices.behavior_board import BehaviorBoard as _BehaviorBoard
from open_rigs.video import SpinnakerCamera as _SpinnakerCamera


class LiveView(BaseSchema):
    """How OpenRigs.LiveView's StreamDevices streams this device; each setting left out keeps its default for the device's kind."""

    enabled: bool = Field(default=True, description="False: the device is not streamed at all.")
    rate: Optional[float] = Field(default=None, gt=0, le=1000, description="Messages per second (camera default 5, behavior board 10).")
    width: Optional[int] = Field(default=None, ge=1, le=4096, description="Width of the streamed frames, in pixels; video only (default 320).")
    height: Optional[int] = Field(default=None, ge=1, le=4096, description="Height of the streamed frames, in pixels; video only (default 240).")


# The device entries take a liveView: block for StreamDevices, which reads rig.yml itself. They
# keep open_rigs' names, so the generated C# classes keep theirs.
class BehaviorBoard(_BehaviorBoard):
    __doc__ = _BehaviorBoard.__doc__
    # Required here, optional on open_rigs' HarpDevice: OpenRigs.HarpDevices checks every board by it.
    usb_serial: str = Field(  # pyright: ignore[reportIncompatibleVariableOverride]
        description="The board's USB serial number, its FTDI chip's (AB0M7PLB): which board this is. OpenRigs.HarpDevices stops the workflow at Start if the board on portName has another. The rig's hardware scan lists each port's (serialPorts, usbSerial)."
    )
    live_view: Optional[LiveView] = Field(default=None, description="Live view settings for this board (OpenRigs.LiveView).")


class SpinnakerCamera(_SpinnakerCamera):
    __doc__ = _SpinnakerCamera.__doc__
    live_view: Optional[LiveView] = Field(default=None, description="Live view settings for this camera (OpenRigs.LiveView).")


# A behaviour box's hardware, the same for every workflow that runs on one, so they all read
# one rig.yml. Each kind of device is a group of named entries, so rig.yml binds a name to its
# hardware (port, serial number) and each workflow asks for the names it drives: an entry it
# does not ask for is ignored, a name it asks for and does not find stops it at startup. The
# name in the data is the kind and the entry joined by a hyphen: behaviorBoard: main: is
# BehaviorBoard-Main. Settings of one experiment belong in that workflow's task schema.
#
# No version (Pip, 3 Nov 2026): a rig's rig.yml is written once at deploy and is then the rig's,
# so a version in it went stale at every change while nothing read it. Whether a workflow takes
# the file is told by its keys instead: the workflow refuses any key its classes lack, and
# rig-setup's rig.yml row names each one a clone on the rig would refuse.
class BehaviourBoxRig(Rig):
    behavior_board: Dict[str, BehaviorBoard] = Field(
        description="Harp Behavior boards by name, such as main (BehaviorBoard-Main): serial port, camera trigger on Trigger0 and running wheel on encoder port 2."
    )
    camera: Dict[str, SpinnakerCamera] = Field(
        description="Triggered Spinnaker cameras by name, such as box (Camera-Box), whose frame rate is the board's trigger0_frequency."
    )
