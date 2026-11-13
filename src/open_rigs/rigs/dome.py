"""The dome rig: one rig.yml model for every workflow that runs on a dome.

Each workflow's rig model is this class, so every dome workflow reads the same rig.yml and a key
added for one is there for all. Device kinds are groups of named entries, as the behaviour box's:
behaviorBoard: main: is BehaviorBoard-Main.
"""
from collections import Counter
from typing import Any, Dict, Iterator

from pydantic import Field, ModelWrapValidatorHandler, model_validator
from swc.aeon.schema import BaseSchema

from open_rigs.components import ProjectionCalibration, SpeakerArray
from open_rigs.core.rig import Rig
from open_rigs.devices import CalibratedPulse, HarpTimestampGeneratorGen3, LicketySplit, StepperPositions
from open_rigs.rigs.behaviour_box import BehaviorBoard, BoardWiring, HarpStepperDriver, SpinnakerCamera

REWARD_VALVE = "RewardValve"
ZAPIT_TRIGGER = "ZapitTrigger"
SIDES = ("left", "right")
WHERE = "(dome rig model, open_rigs.rigs.dome)"
CAMERAS = 3
# How long before its trigger Zapit's armed reply must be in, in seconds (OpenRigs.Devices ZapitTrial.ReplyMarginSeconds):
# every dome task leaves this much between arming Zapit and the trigger.
ZAPIT_REPLY_MARGIN_SECONDS = 0.2
# The longest Zapit ramp-down a dome task may ask for, in seconds (OpenRigs.Devices ZapitConditions.MaximumRampDownMs).
ZAPIT_MAX_RAMP_DOWN_SECONDS = 0.5
# The most one reward may ask for, in ul (OpenRigs.Core LoadRewardLine.DefaultMaxRewardUl, DeliverReward's MaxRewardUl).
MAX_REWARD_UL = 20


class DomeBehaviorBoard(BehaviorBoard):
    __doc__ = BehaviorBoard.__doc__
    wiring: BoardWiring = Field(  # pyright: ignore[reportIncompatibleVariableOverride]
        description="What is plugged into each line: one RewardValve per side, each with its side, and one ZapitTrigger. Reward and Zapit lines are read from here."
    )


class DomeSpeakerArray(SpeakerArray):
    __doc__ = SpeakerArray.__doc__
    error_sound_channel: int = Field(ge=0, description="Mixer output channel the error sound plays on.")

    @model_validator(mode="after")
    def _every_speaker_filtered(self):
        # A dome plays every speaker equalised; OpenRigs.Devices DomeSpeakers.RequireFilters refuses at Start.
        missing = sorted(name for name, speaker in self.speakers.items() if speaker.filter is None)
        if missing:
            raise ValueError(
                f"speakerArray: speaker {', '.join(missing)} has no filter, and a dome plays every speaker equalised {WHERE}. "
                f"Give each its SpeakerFilter artefact, as in `filter: calibration/speaker-{missing[0]}.json`."
            )
        return self


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
    camera: Dict[str, SpinnakerCamera] = Field(
        description="Triggered Spinnaker cameras by name, exactly three: face, pupil and body (Camera-Face, Camera-Pupil, Camera-Body)."
    )
    projection_calibration: ProjectionCalibration = Field(description="Mesh-map and gamma-LUT file paths for the dome projection.")
    speaker_array: DomeSpeakerArray = Field(description="Dome speaker array and audio output device.")
    lick_spout_default_position: StepperPositions = Field(
        description="The stage's in position, in steps, for a mouse with no positions file of its own."
    )
    zapit: ZapitServer = Field(description="Zapit optogenetics server endpoint; contacted only when the task enables optogenetics.")

    @model_validator(mode="wrap")
    @classmethod
    def _no_unknown_keys(cls, data: Any, handler: ModelWrapValidatorHandler["DomeRig"]) -> "DomeRig":
        # The generated C# refuses a key its classes lack at Start (rewardValves, zapit.triggerLine), so this does too.
        if isinstance(data, dict):
            known = {key for field, info in cls.model_fields.items() for key in (field, info.alias)}
            _refuse_unknown([str(key) for key in data if key not in known])
        rig = handler(data)
        if isinstance(data, dict):
            _refuse_unknown(list(_unknown_keys(data, rig.model_dump(by_alias=True, exclude_unset=True))))
        return rig

    @model_validator(mode="after")
    def _wired_lines(self):
        if len(self.camera) != CAMERAS:
            raise ValueError(f"camera lists {len(self.camera)} entries; a dome has exactly {CAMERAS} {WHERE}")
        for name, board in self.behavior_board.items():
            lines = {alias: getattr(board.wiring, field) for field, alias in _ALIASES.items()}
            lines = {alias: line for alias, line in lines.items() if line is not None}
            free = [alias for alias in PULSE_LINES if alias not in lines] or ["DO1"]
            valves = {alias: line for alias, line in lines.items() if line.device == REWARD_VALVE}
            unsided = sorted(alias for alias, line in valves.items() if line.side is None)
            if unsided:
                raise ValueError(
                    f"behaviorBoard {name}: RewardValve on {', '.join(unsided)} has no side in wiring {WHERE}. "
                    f"Give it one, as in `{unsided[0]}: {{ device: RewardValve, side: left }}`."
                )
            sides = Counter(line.side.value for line in valves.values() if line.side is not None)
            for side in SIDES:
                if sides[side] == 0:
                    raise ValueError(
                        f"behaviorBoard {name}: wiring needs exactly one {side} RewardValve, has 0 {WHERE}. "
                        f"Add its line to behaviorBoard.{name}.wiring, as in `{free[0]}: {{ device: RewardValve, side: {side} }}`."
                    )
                if sides[side] > 1:
                    raise ValueError(
                        f"behaviorBoard {name}: wiring needs exactly one {side} RewardValve, has {sides[side]} {WHERE}. "
                        f"Keep the line the {side} valve is plugged into, as in `DO1: {{ device: RewardValve, side: {side} }}`, "
                        "and change the others."
                    )
            zapit = sum(line.device == ZAPIT_TRIGGER for line in lines.values())
            if zapit == 0:
                raise ValueError(
                    f"behaviorBoard {name}: wiring needs exactly one ZapitTrigger, has 0 {WHERE}. "
                    f"Add its line to behaviorBoard.{name}.wiring, as in `{free[0]}: {{ device: ZapitTrigger }}`."
                )
            if zapit > 1:
                raise ValueError(
                    f"behaviorBoard {name}: wiring needs exactly one ZapitTrigger, has {zapit} {WHERE}. "
                    "Keep the line Zapit's trigger is plugged into, as in `DO3: { device: ZapitTrigger }`, and change the others."
                )
            outputs = board.pulse_controller.outputs if board.pulse_controller else None
            for alias in sorted(valves):
                output = getattr(outputs, alias.lower(), None) if outputs else None
                if output is None or not isinstance(output.root, CalibratedPulse):
                    raise ValueError(
                        f"behaviorBoard {name}: RewardValve {alias} needs a CalibratedPulse in pulseController.outputs {WHERE}. "
                        f"Add it, as in `{alias}: {{ discriminatorType: CalibratedPulse, artefact: calibration/valve-{alias}.json }}`."
                    )
        return self


_ALIASES = {field: info.alias for field, info in BoardWiring.model_fields.items()}
PULSE_LINES = ("DO1", "DO2", "DO3")


def _unknown_keys(given: Any, kept: Any, path: str = "") -> Iterator[str]:
    """The keys of `given` (rig.yml as read) that validating it did not keep in `kept` (the rig dumped by alias)."""
    if isinstance(given, dict) and isinstance(kept, dict):
        for key, value in given.items():
            if key not in kept:
                yield f"{path}{key}"
            else:
                yield from _unknown_keys(value, kept[key], f"{path}{key}.")
    elif isinstance(given, list) and isinstance(kept, list):
        for index, (value, kept_value) in enumerate(zip(given, kept)):
            yield from _unknown_keys(value, kept_value, f"{path}{index}.")


def _refuse_unknown(keys: list[str]) -> None:
    if keys:
        raise ValueError(
            f"rig.yml has {', '.join(sorted(keys))}, which a dome does not use {WHERE}. "
            "The workflow refuses them at Start: delete them from rig.yml."
        )
