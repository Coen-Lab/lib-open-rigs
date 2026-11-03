from typing import Dict, Literal
from pydantic import Field
from swc.aeon.schema import BaseSchema
from open_rigs.devices.serial import SerialDevice
from open_rigs.devices.harp import HarpStepperDriver
import open_rigs.core.base as data_types


class StepperPositions(BaseSchema):
    """
    Absolute target position for the 5-axis spout rig, expressed in task-relative axes.
    """

    left_elevation: int = Field(
        description="Left spout elevation axis absolute position (steps). Maps to motor 1"
    )
    right_elevation: int = Field(
        description="Right spout elevation axis absolute position (steps). Maps to motor 2"
    )
    right_radial: int = Field(
        description="Right spout radial axis (in/out) absolute position (steps). Maps to motor 3"
    )
    left_radial: int = Field(
        description="Left spout radial axis (in/out) absolute position (steps). Maps to motor 4"
    )
    base_transverse: int = Field(
        description="Base transverse axis absolute position (steps). Maps to motor 5"
    )


class SpoutRigPosition(BaseSchema):
    """
    Dictionary of named absolute positions, e.g.:
      home, both_in, both_out
    """

    positions: Dict[str, StepperPositions] = Field(
        default_factory=dict,
        description="Named absolute positions of the lick spout stage stepper rig, keyed by a string identifier.",
        examples=[
            {
                "home": {
                    "left_elevation": 0,
                    "right_elevation": 0,
                    "right_radial": 0,
                    "left_radial": 0,
                    "base_transverse": 0,
                },
                "both_in": {
                    "left_elevation": 1000,
                    "right_elevation": 1000,
                    "right_radial": 2000,
                    "left_radial": 2000,
                    "base_transverse": 500,
                },
                "both_out": {
                    "left_elevation": 1000,
                    "right_elevation": 1000,
                    "right_radial": 1000,
                    "left_radial": 1000,
                    "base_transverse": 500,
                },
            }
        ],
    )


class LickSpoutStageDriver(SerialDevice):
    """Represents an Arduino device driving stepper motors controlling a lick spout stage."""

    # Protocol command bytes
    move: data_types.Byte = Field(default=71, description="Command byte for MOVE.")
    set_speed: data_types.Byte = Field(default=72, description="Command byte for SET SPEED.")
    set_acceleration: data_types.Byte = Field(default=73, description="Command byte for SET ACCELERATION.")

    # Motion parameters
    speed: int = Field(default=300, description="Default motor speed.")
    acceleration_major: int = Field(default=20, description="Major acceleration component.")
    acceleration_minor: int = Field(default=2, description="Minor acceleration component.")

    # Set positions
    set_position: SpoutRigPosition


class MotorAddress(BaseSchema):
    """Identifies a specific motor on a specific stepper driver board."""

    board: Literal["a", "b"] = Field(description='Which board drives this axis: "a" or "b".')
    motor: int = Field(ge=0, le=3, description="Motor index on the board (0-3).")


class StageAxisMapping(BaseSchema):
    """
    Maps each of the 5 stage axes to a (board, motor) address.
    Editing only the YAML re-routes axes across boards without touching the Bonsai workflow.

    Default layout:
        Board A: left_elevation(0), right_elevation(1), right_radial(2), left_radial(3)
        Board B: base_transverse(0)
    """

    left_elevation: MotorAddress = Field(default=MotorAddress(board="a", motor=0))
    right_elevation: MotorAddress = Field(default=MotorAddress(board="a", motor=1))
    right_radial: MotorAddress = Field(default=MotorAddress(board="a", motor=2))
    left_radial: MotorAddress = Field(default=MotorAddress(board="a", motor=3))
    base_transverse: MotorAddress = Field(default=MotorAddress(board="b", motor=0))


class HarpLickSpoutStage(BaseSchema):
    """
    Harp-based lick spout stage: two StepperDriver boards driving a 5-axis rig.
    Calibration (named positions) is stored separately in a SpoutRigPosition YAML file.
    """

    driver_a: HarpStepperDriver = Field(description="Primary StepperDriver board.")
    driver_b: HarpStepperDriver = Field(description="Secondary StepperDriver board.")
    axis_mapping: StageAxisMapping = Field(
        default=StageAxisMapping(),
        description="Maps each stage axis to its board and motor index.",
    )
