from pydantic import Field
from swc.aeon.schema import BaseSchema


class SpoutPositions(BaseSchema):
    """
    The lick spout stage's five axes, in motor steps from each axis's home switch.
    """

    base_transverse: int = Field(description="Base transverse axis (StepperDriver-A Motor0), in steps.")
    radial_left: int = Field(description="Left spout radial axis (StepperDriver-A Motor1), in steps; more is towards the mouse.")
    radial_right: int = Field(description="Right spout radial axis (StepperDriver-A Motor2), in steps; more is towards the mouse.")
    elevation_left: int = Field(description="Left spout elevation axis (StepperDriver-B Motor0), in steps.")
    elevation_right: int = Field(description="Right spout elevation axis (StepperDriver-B Motor1), in steps.")
