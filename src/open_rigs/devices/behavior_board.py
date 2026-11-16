from typing import Annotated, Optional, Union

from pydantic import Field, RootModel
from pydantic.json_schema import JsonSchemaValue
from swc.aeon.schema import BaseSchema
from open_rigs.devices.harp import HarpBehavior
from open_rigs.core.artefacts import ArtefactPath
from open_rigs.core.base import DiscriminatorTypeMixin, UShort, bind_typename


class DigitalOutputs(RootModel[str]):
    """A set of behavior board digital output lines, written comma separated."""

    @classmethod
    def __get_pydantic_json_schema__(cls, core_schema, handler) -> JsonSchemaValue:
        """Binds the device's own flags enum, so generated properties can be assigned it."""
        return bind_typename(handler(core_schema), "Harp.Behavior.DigitalOutputs")


class CameraTriggerController(BaseSchema):
    """Represents a CameraTriggerController module on a BehaviorBoard device.

    The two camera trigger outputs share pins with digital outputs: Trigger0 is DO0 and Trigger1
    is DO1. A trigger that is not enabled is never started, which leaves its pin free for a pulse
    output such as a reward valve.
    """

    trigger0_enabled: bool = Field(
        default=True, description="Start camera trigger 0 (CameraOutput0, the DO0 pin) when the rig starts."
    )
    trigger0_frequency: int = Field(
        examples=["50"],
        description="The frequency, in Hz, at which to emit camera triggers on Trigger0 (DO0, CameraOutput0)",
    )
    trigger1_enabled: bool = Field(
        default=False,
        description="Start camera trigger 1 (CameraOutput1, the DO1 pin) when the rig starts. Leave off when DO1 drives a pulse output.",
    )
    trigger1_frequency: int = Field(
        examples=["50"],
        description="The frequency, in Hz, at which to emit camera triggers on Trigger1 (DO1, CameraOutput1). Only matters when trigger 1 is enabled.",
    )


class FixedPulse(DiscriminatorTypeMixin, BaseSchema):
    """A pulsed output whose width is set in the rig configuration, such as an LED, an air puff or a TTL."""

    pulse_width_ms: UShort = Field(description="Pulse duration, in milliseconds.")


class CalibratedPulse(DiscriminatorTypeMixin, BaseSchema):
    """A pulsed output whose width comes from a calibration curve's linear fit at run time, such as a reward valve."""

    artefact: ArtefactPath = Field(
        examples=["calibration/valve-DO1.json"],
        description=(
            "Relative path of the CalibrationCurve that converts the requested quantity to a pulse width: "
            "a file in this machine's calibration folder holding the measured points and their linear fit."
        ),
    )


class PulseOutput(
    RootModel[Annotated[Union[FixedPulse, CalibratedPulse], Field(discriminator="discriminator_type")]]
):
    """What drives the pulse width of one digital output line: a fixed width or a calibration curve."""

    pass


class PulseOutputs(BaseSchema):
    """The pulse source of each behavior board digital output line. Each line appears once.

    DO1 to DO3 are required, an unused one written as a FixedPulse with a width of 0; DO0 and
    DOPort0 to DOPort2, the board's other pulsed lines, are given only when one is used (a reward
    valve on it).
    """

    do0: Optional[PulseOutput] = Field(default=None, alias="DO0", description="Pulse source for DO0, when it is pulsed.")
    do1: PulseOutput = Field(alias="DO1", description="Pulse source for DO1.")
    do2: PulseOutput = Field(alias="DO2", description="Pulse source for DO2.")
    do3: PulseOutput = Field(alias="DO3", description="Pulse source for DO3; a FixedPulse of width 0 if unused.")
    do_port0: Optional[PulseOutput] = Field(default=None, alias="DOPort0", description="Pulse source for DOPort0, when it is pulsed.")
    do_port1: Optional[PulseOutput] = Field(default=None, alias="DOPort1", description="Pulse source for DOPort1, when it is pulsed.")
    do_port2: Optional[PulseOutput] = Field(default=None, alias="DOPort2", description="Pulse source for DOPort2, when it is pulsed.")


class PulseController(BaseSchema):
    """Represents the PulseController module on the BehaviorBoard."""

    output_pulse_enable: DigitalOutputs = Field(
        default=DigitalOutputs("DO1, DO2, DO3"),
        description="Digital output lines enabled for pulse generation, comma separated.",
    )
    outputs: PulseOutputs = Field(description="Pulse source of each digital output line.")


class RunningWheel(BaseSchema):
    """Represents configuration parameters of the RunningWheel module.
    Exposes wheel geometry parameters used to compute speed and distance from encoder counts.
    """

    counts_per_rev: int = Field(
        description="Number of encoder counts per full revolution of the running wheel."
    )
    wheel_diameter_mm: float = Field(description="The diameter of the running wheel, in millimetres.")


class BehaviorBoard(HarpBehavior):
    """Represents a Harp Behavior Board device."""

    pulse_controller: PulseController | None = Field(
        default=None, description="Optional PulseController module for generating digital output pulses."
    )
    camera_trigger_controller: CameraTriggerController | None = Field(
        default=None,
        description="Optional CameraTriggerController module for emitting camera trigger pulses.",
    )
    running_wheel: RunningWheel | None = Field(
        default=None, description="Optional RunningWheelModule module to define wheel geometry."
    )
