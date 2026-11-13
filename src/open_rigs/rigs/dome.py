"""The dome rig: one rig.yml model for every workflow that runs on a dome.

Each workflow's rig model is this class, so every dome workflow reads the same rig.yml and a key
added for one is there for all. Device kinds are groups of named entries, as the behaviour box's:
behaviorBoard: main: is BehaviorBoard-Main.
"""
from collections import Counter
from typing import Dict

from pydantic import Field, model_validator
from swc.aeon.schema import BaseSchema

from open_rigs.components import ProjectionCalibration, SpeakerArray
from open_rigs.core.rig import Rig
from open_rigs.devices import CalibratedPulse, HarpTimestampGeneratorGen3, LicketySplit, StepperPositions
from open_rigs.rigs.behaviour_box import BehaviorBoard, BoardWiring, HarpStepperDriver, SpinnakerCamera

REWARD_VALVE = "RewardValve"
ZAPIT_TRIGGER = "ZapitTrigger"
SIDES = ("left", "right")


class DomeBehaviorBoard(BehaviorBoard):
    __doc__ = BehaviorBoard.__doc__
    wiring: BoardWiring = Field(  # pyright: ignore[reportIncompatibleVariableOverride]
        description="What is plugged into each line: one RewardValve per side, each with its side, and one ZapitTrigger. Reward and Zapit lines are read from here."
    )


class DomeSpeakerArray(SpeakerArray):
    __doc__ = SpeakerArray.__doc__
    error_sound_channel: int = Field(ge=0, description="Mixer output channel the error sound plays on.")


class ZapitServer(BaseSchema):
    """TCP endpoint of the Zapit optogenetics server (tcpServer section of the Zapit settings file). The trigger line is the board's ZapitTrigger in wiring."""

    host: str = Field(default="127.0.0.1", description="Host name or IP address of the PC running Zapit.")
    port: int = Field(default=1488, ge=1, le=65535, description="TCP port the Zapit server listens on.")
    hardware_triggered: bool = Field(
        default=False,
        description="True: Zapit arms its DAQ on the TCP command and the ZapitTrigger line starts the stimulus. False: Zapit starts the stimulus on receipt of the command.",
    )


class DomeRig(Rig):
    """A dome: Behavior board, timestamp generator, two-driver lick spout stage, two lick detectors,
    cameras, projection, speaker array and Zapit."""

    behavior_board: Dict[str, DomeBehaviorBoard] = Field(
        description="Harp Behavior boards by name: main (BehaviorBoard-Main) drives the valves, camera trigger, Zapit trigger and wheel encoder."
    )
    timestamp_generator_gen3: Dict[str, HarpTimestampGeneratorGen3] = Field(
        description="Harp timestamp generators by name: main (TimestampGeneratorGen3-Main) syncs the boards' clocks."
    )
    stepper_driver: Dict[str, HarpStepperDriver] = Field(
        description="Harp StepperDriver boards by name: a and b (StepperDriver-A, StepperDriver-B) drive the lick spout stage's five axes."
    )
    lickety_split: Dict[str, LicketySplit] = Field(
        description="Lick detectors by name: left (LicketySplit-Left) and right (LicketySplit-Right)."
    )
    camera: Dict[str, SpinnakerCamera] = Field(description="Triggered Spinnaker cameras by name, such as face (Camera-Face).")
    projection_calibration: ProjectionCalibration = Field(description="Mesh-map and gamma-LUT file paths for the dome projection.")
    speaker_array: DomeSpeakerArray = Field(description="Dome speaker array and audio output device.")
    lick_spout_default_position: StepperPositions = Field(
        description="The stage's in position, in steps, for a mouse with no positions file of its own."
    )
    zapit: ZapitServer = Field(description="Zapit optogenetics server endpoint; contacted only when the task enables optogenetics.")

    @model_validator(mode="after")
    def _wired_lines(self):
        for name, board in self.behavior_board.items():
            lines = {alias: getattr(board.wiring, field) for field, alias in _ALIASES.items()}
            lines = {alias: line for alias, line in lines.items() if line is not None}
            valves = {alias: line for alias, line in lines.items() if line.device == REWARD_VALVE}
            unsided = sorted(alias for alias, line in valves.items() if line.side is None)
            if unsided:
                raise ValueError(f"behaviorBoard {name}: RewardValve on {', '.join(unsided)} has no side")
            sides = Counter(line.side.value for line in valves.values() if line.side is not None)
            for side in SIDES:
                if sides[side] != 1:
                    raise ValueError(f"behaviorBoard {name}: wiring needs exactly one {side} RewardValve, has {sides[side]}")
            zapit = sum(line.device == ZAPIT_TRIGGER for line in lines.values())
            if zapit != 1:
                raise ValueError(f"behaviorBoard {name}: wiring needs exactly one ZapitTrigger, has {zapit}")
            outputs = board.pulse_controller.outputs if board.pulse_controller else None
            for alias in sorted(valves):
                output = getattr(outputs, alias.lower(), None) if outputs else None
                if output is None or not isinstance(output.root, CalibratedPulse):
                    raise ValueError(
                        f"behaviorBoard {name}: RewardValve {alias} needs a CalibratedPulse in pulseController.outputs"
                    )
        return self


_ALIASES = {field: info.alias for field, info in BoardWiring.model_fields.items()}
