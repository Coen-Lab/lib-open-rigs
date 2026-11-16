from pydantic import Field
from swc.aeon.schema import BaseSchema


class StepperPositions(BaseSchema):
    """
    Absolute target position for the 5-axis spout rig, expressed in task-relative axes.
    """

    left_elevation: int = Field(
        description="Left spout elevation axis absolute position (steps). Maps to motor 4 (StepperDriver b, motor 0)"
    )
    right_elevation: int = Field(
        description="Right spout elevation axis absolute position (steps). Maps to motor 5 (StepperDriver b, motor 1)"
    )
    right_radial: int = Field(
        description="Right spout radial axis (in/out) absolute position (steps). Maps to motor 3 (StepperDriver a, motor 2)"
    )
    left_radial: int = Field(
        description="Left spout radial axis (in/out) absolute position (steps). Maps to motor 2 (StepperDriver a, motor 1)"
    )
    base_transverse: int = Field(
        description="Base transverse axis absolute position (steps). Maps to motor 1 (StepperDriver a, motor 0)"
    )
