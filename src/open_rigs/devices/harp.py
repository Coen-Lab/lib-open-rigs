from enum import StrEnum
from typing import ClassVar, Literal
from pydantic import Field
from pydantic.json_schema import JsonSchemaValue
from swc.aeon.schema import BaseSchema

from open_rigs.core.base import bind_typename


class TimerFrequency(StrEnum):
    Disabled = "Disabled"
    Timer50Hz = "Timer50Hz"
    Timer100Hz = "Timer100Hz"
    Timer200Hz = "Timer200Hz"
    Timer500Hz = "Timer500Hz"
    Timer1000Hz = "Timer1000Hz"

    @classmethod
    def __get_pydantic_json_schema__(cls, core_schema, handler) -> JsonSchemaValue:
        """Binds the device's own enum, so generated properties can be assigned it directly."""
        return bind_typename(handler(core_schema), "Harp.TimestampGeneratorGen3.TimerRate")


class HarpDevice(BaseSchema):
    who_am_i: ClassVar[int] = Field(description="The unique identifier for the device type.")
    port_name: str = Field(examples=["COM"], description="The name of the device serial port.")


class HarpClockSynchronizer(HarpDevice):
    who_am_i: ClassVar[int] = 1152


class HarpTimestampGeneratorGen3(HarpDevice):
    """Harp Timestamp Generator Gen3 (who_am_i=1158). Provides hardware clock synchronisation."""

    who_am_i: ClassVar[int] = 1158
    timer_frequency: TimerFrequency = Field(
        default=TimerFrequency.Timer1000Hz,
        description="Frequency of the timer output signal.",
    )


class HarpCameraControllerGen2(HarpDevice):
    who_am_i: ClassVar[int] = 1170


class HarpBehavior(HarpDevice):
    who_am_i: ClassVar[int] = 1216


class HarpHobgoblin(HarpDevice):
    who_am_i: ClassVar[int] = 123


class HarpStepperDriver(HarpDevice):
    """Single Harp StepperDriver board (who_am_i=1130). Controls up to 4 motors (0-3)."""

    who_am_i: ClassVar[int] = 1130
    step_interval: int = Field(
        default=250,
        description="Step interval in µs (100-20000). Lower = faster.",
    )
    microstep_resolution: Literal["Full", "Half", "Microstep4", "Microstep8", "Microstep16", "Microstep32"] = Field(
        default="Microstep8",
        description="Microstep resolution. Affects how many steps equal one physical unit.",
    )
    maximum_run_current: float = Field(
        default=0.5,
        ge=0.0,
        le=2.0,
        description="Maximum run current in amps (0.0-2.0).",
    )
