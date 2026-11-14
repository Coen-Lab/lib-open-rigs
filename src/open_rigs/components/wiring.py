"""What is plugged into each line of a rig's boards, by the boards' own channel names."""
from enum import Enum
from typing import List, Optional

from pydantic import Field
from swc.aeon.schema import BaseSchema


class Side(str, Enum):
    """Which side of the animal a line serves."""

    LEFT = "left"
    RIGHT = "right"


class WiredLine(BaseSchema):
    """What is plugged into one line of the board. Nothing compares it with a workflow at Start; a
    rig's reward and Zapit lines are read from it. part and note are for people."""

    device: str = Field(
        examples=["RewardValve", "CameraTrigger", "Photodiode", "ZapitTrigger"],
        description=r"What is on the line: one of the wiredDevices in the lab's device dictionary (config\device-dictionary.json on its share). Nothing checks it against that list.",
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
    line left out is unknown."""

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
    driver's own names: the board stays a board, recorded as itself, and a spare motor can drive
    anything else under its own name. A line left out is unknown."""

    motor0: Optional[WiredLine] = _wired("Motor0", "motor output 0")
    motor1: Optional[WiredLine] = _wired("Motor1", "motor output 1")
    motor2: Optional[WiredLine] = _wired("Motor2", "motor output 2")
    motor3: Optional[WiredLine] = _wired("Motor3", "motor output 3")
    input0: Optional[WiredLine] = _wired("Input0", "digital input 0")
    input1: Optional[WiredLine] = _wired("Input1", "digital input 1")
    input2: Optional[WiredLine] = _wired("Input2", "digital input 2")
    input3: Optional[WiredLine] = _wired("Input3", "digital input 3")
