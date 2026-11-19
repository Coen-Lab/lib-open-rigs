from pydantic import Field
from swc.aeon.schema import BaseSchema


class SpoutPositions(BaseSchema):
    """
    The lick spout stage's five axes, in motor steps from each axis's home switch. Each axis's motor is
    the stepperDriver motor whose wiring names it.
    """

    base_transverse: int = Field(description="Base transverse axis (the motor wired BaseTransverse), in steps.")
    radial_left: int = Field(description="Left spout radial axis (the motor wired Radial, side left), in steps; more is towards the mouse.")
    radial_right: int = Field(description="Right spout radial axis (the motor wired Radial, side right), in steps; more is towards the mouse.")
    elevation_left: int = Field(description="Left spout elevation axis (the motor wired Elevation, side left), in steps.")
    elevation_right: int = Field(description="Right spout elevation axis (the motor wired Elevation, side right), in steps.")
