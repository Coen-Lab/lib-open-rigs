from enum import Enum
from typing import Dict, List, Literal, Optional

from pydantic import Field
from swc.aeon.schema import BaseSchema

from open_rigs.core.rig import Rig
from open_rigs.devices.behavior_board import BehaviorBoard as _BehaviorBoard
from open_rigs.devices.harp import HarpStepperDriver as _HarpStepperDriver
from open_rigs.video import SpinnakerCamera as _SpinnakerCamera


class LiveView(BaseSchema):
    """How OpenRigs.LiveView's StreamDevices streams this device; each setting left out keeps its default for the device's kind."""

    enabled: bool = Field(default=True, description="False: the device is not streamed at all.")
    rate: Optional[float] = Field(default=None, gt=0, le=1000, description="Messages per second (camera default 5, behavior board 10).")
    width: Optional[int] = Field(default=None, ge=1, le=4096, description="Width of the streamed frames, in pixels; video only (default 320).")
    height: Optional[int] = Field(default=None, ge=1, le=4096, description="Height of the streamed frames, in pixels; video only (default 240).")


class Side(str, Enum):
    """Which side of the animal a line serves."""

    LEFT = "left"
    RIGHT = "right"


class WiredLine(BaseSchema):
    """What is plugged into one line of the board. A workflow's schemas/board-lines.yml says what it
    uses each line for, and OpenRigs.HarpDevices compares the two at Start: device, side and targets
    must agree on every line the workflow uses, or the workflow stops. part and note are for people."""

    device: str = Field(
        examples=["RewardValve", "CameraTrigger", "Photodiode", "ZapitTrigger"],
        description=r"What is on the line, as the workflow's board-lines.yml names it: one of the lab's wiredDevices in K:\config\device-dictionary.json, checked by app-rig-control's names check.",
    )
    side: Optional[Side] = Field(default=None, description="The side it serves, where that matters (a reward valve).")
    targets: Optional[List[str]] = Field(
        default=None,
        examples=[["Camera-Face", "Camera-Pupil", "Camera-Body"]],
        description="The rig's devices this line drives or reads, by their Kind-Instance names in this rig.yml.",
    )
    part: Optional[str] = Field(default=None, description="The physical part, for people; never compared.")
    note: Optional[str] = Field(default=None, description="Anything else about the line (a cable label, a patch panel), for people; never compared.")


def _wired(channel: str, what: str) -> Optional[WiredLine]:
    return Field(default=None, alias=channel, description=f"What is plugged into {channel} ({what}); left out when unknown.")


class BoardWiring(BaseSchema):
    """What is plugged into each line of a Harp Behavior board, by the board's own channel names. A
    line left out is unknown, which is allowed only for a line no workflow on the rig uses."""

    di_port0: Optional[WiredLine] = _wired("DIPort0", "digital input, port 0")
    di_port1: Optional[WiredLine] = _wired("DIPort1", "digital input, port 1")
    di_port2: Optional[WiredLine] = _wired("DIPort2", "digital input, port 2")
    di3: Optional[WiredLine] = _wired("DI3", "digital input 3")
    dio0: Optional[WiredLine] = _wired("DIO0", "port 0 digital input/output")
    dio1: Optional[WiredLine] = _wired("DIO1", "port 1 digital input/output")
    dio2: Optional[WiredLine] = _wired("DIO2", "port 2 digital input/output")
    do0: Optional[WiredLine] = _wired("DO0", "digital output 0, camera trigger 0's pin")
    do1: Optional[WiredLine] = _wired("DO1", "digital output 1, camera trigger 1's pin")
    do2: Optional[WiredLine] = _wired("DO2", "digital output 2")
    do3: Optional[WiredLine] = _wired("DO3", "digital output 3")
    do_port0: Optional[WiredLine] = _wired("DOPort0", "digital output, port 0")
    do_port1: Optional[WiredLine] = _wired("DOPort1", "digital output, port 1")
    do_port2: Optional[WiredLine] = _wired("DOPort2", "digital output, port 2")
    supply_port0: Optional[WiredLine] = _wired("SupplyPort0", "port 0 supply")
    supply_port1: Optional[WiredLine] = _wired("SupplyPort1", "port 1 supply")
    supply_port2: Optional[WiredLine] = _wired("SupplyPort2", "port 2 supply")
    led0: Optional[WiredLine] = _wired("Led0", "LED 0")
    led1: Optional[WiredLine] = _wired("Led1", "LED 1")
    rgb0: Optional[WiredLine] = _wired("Rgb0", "RGB LED 0")
    rgb1: Optional[WiredLine] = _wired("Rgb1", "RGB LED 1")
    analog_input0: Optional[WiredLine] = _wired("AnalogInput0", "analog input 0, ADC0")
    analog_input1: Optional[WiredLine] = _wired("AnalogInput1", "analog input 1, ADC1")


class StepperWiring(BaseSchema):
    """What each motor output and digital input of a Harp StepperDriver is connected to, by the
    driver's own names (Pip, 11 Nov 2026): the board stays a board, recorded as itself, and a spare
    motor can drive anything else under its own name. A line left out is unknown, which is allowed
    only for a line no workflow on the rig uses."""

    motor0: Optional[WiredLine] = _wired("Motor0", "motor output 0")
    motor1: Optional[WiredLine] = _wired("Motor1", "motor output 1")
    motor2: Optional[WiredLine] = _wired("Motor2", "motor output 2")
    motor3: Optional[WiredLine] = _wired("Motor3", "motor output 3")
    input0: Optional[WiredLine] = _wired("Input0", "digital input 0")
    input1: Optional[WiredLine] = _wired("Input1", "digital input 1")
    input2: Optional[WiredLine] = _wired("Input2", "digital input 2")
    input3: Optional[WiredLine] = _wired("Input3", "digital input 3")


class HarpStepperDriver(_HarpStepperDriver):
    __doc__ = _HarpStepperDriver.__doc__
    wiring: Optional[StepperWiring] = Field(
        default=None,
        description="What each motor and input is connected to (Pip, 11 Nov 2026): checked at Start against the lines the workflow uses (its schemas/board-lines.yml) by OpenRigs.HarpDevices.",
    )


# The device entries take a liveView: block for StreamDevices, which reads rig.yml itself. They
# keep open_rigs' names, so the generated C# classes keep theirs.
class BehaviorBoard(_BehaviorBoard):
    __doc__ = _BehaviorBoard.__doc__
    # Required here, optional on open_rigs' HarpDevice: OpenRigs.HarpDevices checks every board by it.
    usb_serial: str = Field(  # pyright: ignore[reportIncompatibleVariableOverride]
        description="The board's USB serial number, its FTDI chip's (AB0M7PLB): which board this is. OpenRigs.HarpDevices stops the workflow at Start if the board on portName has another. The rig's hardware scan lists each port's (serialPorts, usbSerial)."
    )
    live_view: Optional[LiveView] = Field(default=None, description="Live view settings for this board (OpenRigs.LiveView).")
    wiring: Optional[BoardWiring] = Field(
        default=None,
        description="What is plugged into each line (Pip, 7 Nov 2026): checked at Start against the lines the workflow uses (its schemas/board-lines.yml) by OpenRigs.HarpDevices.",
    )


class SpinnakerCamera(_SpinnakerCamera):
    __doc__ = _SpinnakerCamera.__doc__
    live_view: Optional[LiveView] = Field(default=None, description="Live view settings for this camera (OpenRigs.LiveView).")
    # Only formats LogVideo writes: its FFmpeg writer takes 8-bit frames alone (audit, 11 Nov 2026).
    pixel_format: Literal["Mono8"] = Field(
        default="Mono8",
        description="The pixel format the camera sends, a Spinnaker PixelFormat name LogVideo can write: Mono8 (Pip, 11 Nov 2026). Set each time the camera opens, so a format stored on the camera is never used. Mono8 when not set.",
    )


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
# app-rig-setup's rig.yml row names each one a clone on the rig would refuse.
class BehaviourBoxRig(Rig):
    behavior_board: Dict[str, BehaviorBoard] = Field(
        description="Harp Behavior boards by name, such as main (BehaviorBoard-Main): serial port, camera trigger on Trigger0 and running wheel on encoder port 2."
    )
    camera: Dict[str, SpinnakerCamera] = Field(
        description="Triggered Spinnaker cameras by name, such as box (Camera-Box), whose frame rate is the board's trigger0_frequency."
    )
