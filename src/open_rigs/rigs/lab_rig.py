"""The lab's rig: one rig.yml model for every workflow, whatever the rig.

Every part is optional, so a rig.yml lists the parts its rig has and each workflow takes the ones
it drives; a key added for one workflow is there for all. Each device kind is a group of named
entries: behaviorBoard: main: is BehaviorBoard-Main. An entry a workflow does not ask for is
ignored, and a name it asks for and does not find stops it at Start. Settings of one experiment
belong in that workflow's task schema.

No version: a rig's rig.yml is written once at deploy and is then the rig's, so a version in it
went stale at every change while nothing read it. Whether a workflow takes the file is told by its
keys instead: the workflow refuses any key its classes lack, and so does this model.
"""
from collections import Counter
from typing import Any, Dict, Iterator, Optional

from pydantic import Field, ModelWrapValidatorHandler, model_validator

from open_rigs.components import ProjectionCalibration
from open_rigs.components.harp_boards import BehaviorBoard, HarpStepperDriver
from open_rigs.components.speakers import EqualisedSpeakerArray
from open_rigs.components.video import SpinnakerCamera, VideoEncoder
from open_rigs.components.wiring import BoardWiring
from open_rigs.components.zapit import ZapitServer
from open_rigs.core.rig import Rig
from open_rigs.devices import CalibratedPulse, HarpTimestampGeneratorGen3, LicketySplit, StepperPositions

REWARD_VALVE = "RewardValve"
ZAPIT_TRIGGER = "ZapitTrigger"
CAMERA_TRIGGER = "CameraTrigger"
SIDES = ("left", "right")
WHERE = "(lab rig model, open_rigs.rigs.lab_rig)"
PLACEHOLDER = "CHANGE-ME"  # rig-template.yml's mark on each value a rig fills in
# The rig.yml kinds a preflight lists, in its order (OpenRigs.Devices PreflightDevices.AllKinds, held equal by a test): each
# device whose data the preflight counts. Not zapit: Zapit answers only when commanded, so a preflight has no data to count.
PREFLIGHT_KINDS = ("behaviorBoard", "timestampGeneratorGen3", "stepperDriver", "licketySplit", "camera", "speakerArray",
                   "projectionCalibration")
# The most one reward may ask for, in ul (OpenRigs.Core LoadRewardLine.DefaultMaxRewardUl, DeliverReward's MaxRewardUl).
MAX_REWARD_UL = 20


class LabRig(Rig):
    """A rig's parts, each optional: Behavior boards, timestamp generators, stepper drivers (a lick
    spout stage), lick detectors, cameras, projection, a speaker array, Zapit and the video encoder."""

    behavior_board: Dict[str, BehaviorBoard] = Field(
        default_factory=dict,
        description="Harp Behavior boards by name, such as main (BehaviorBoard-Main): valves, camera trigger, Zapit trigger and running wheel.",
    )
    timestamp_generator_gen3: Dict[str, HarpTimestampGeneratorGen3] = Field(
        default_factory=dict,
        description="Harp timestamp generators by name, such as main (TimestampGeneratorGen3-Main), which syncs the boards' clocks.",
    )
    stepper_driver: Dict[str, HarpStepperDriver] = Field(
        default_factory=dict,
        description="Harp StepperDriver boards by name, such as a and b (StepperDriver-A, StepperDriver-B), which drive a lick spout stage's five axes.",
    )
    lickety_split: Dict[str, LicketySplit] = Field(
        default_factory=dict,
        description="Lick detectors by name, such as left and right (LicketySplit-Left, LicketySplit-Right).",
    )
    camera: Dict[str, SpinnakerCamera] = Field(
        default_factory=dict,
        description="Triggered Spinnaker cameras by name, such as face (Camera-Face), each on the board trigger whose wiring line (device CameraTrigger, DO0 or DO1) names it in targets, at that trigger's frequency.",
    )
    projection_calibration: Optional[ProjectionCalibration] = Field(
        default=None, description="Mesh-map and gamma-LUT files for the projector's display, each relative to rig.yml's folder."
    )
    speaker_array: Optional[EqualisedSpeakerArray] = Field(default=None, description="Speaker array and audio output device.")
    lick_spout_default_position: Optional[StepperPositions] = Field(
        default=None, description="The lick spout stage's in position, in steps, for a mouse with no positions file of its own."
    )
    zapit: Optional[ZapitServer] = Field(
        default=None, description="Zapit optogenetics server endpoint; contacted only when the task enables optogenetics."
    )
    video_encoder: Optional[VideoEncoder] = Field(default=None, description="How the cameras' video is encoded; left out: libx264 and the ffmpeg.exe on PATH.")

    @model_validator(mode="wrap")
    @classmethod
    def _no_unknown_keys(cls, data: Any, handler: ModelWrapValidatorHandler["LabRig"]) -> "LabRig":
        # The generated C# refuses a key its classes lack at Start (rewardValves, zapit.triggerLine), so this does too.
        if isinstance(data, dict):
            known = {key for field, info in cls.model_fields.items() for key in (field, info.alias)}
            _refuse_unknown([str(key) for key in data if key not in known])
            unfilled = sorted(_placeholders(data))
            if unfilled:
                raise ValueError(
                    f"rig.yml still has {PLACEHOLDER} at {', '.join(unfilled)} {WHERE}. Fill in this rig's value for each."
                )
        rig = handler(data)
        if isinstance(data, dict):
            # A rig built in Python names its parts by field (video_encoder), which the dump below names by alias.
            aliases = {field: info.alias or field for field, info in cls.model_fields.items()}
            given = {aliases.get(str(key), key): value for key, value in data.items()}
            _refuse_unknown(list(_unknown_keys(given, rig.model_dump(by_alias=True, exclude_unset=True))))
        return rig

    def reward_valve_lines(self) -> set[int]:
        """The DO lines (1 to 3) wiring names as a RewardValve, on every board (for
        workflow_lint.fixed_width_pulses_on_reward_valves)."""
        return {int(alias.removeprefix("DO")) for board in self.behavior_board.values() if board.wiring
                for field, alias in _ALIASES.items()
                if (line := getattr(board.wiring, field)) is not None and line.device == REWARD_VALVE}

    @model_validator(mode="after")
    def _wired_lines(self):
        zapit_boards, valve_boards = [], []
        for name, board in self.behavior_board.items():
            lines = {alias: getattr(board.wiring, field) for field, alias in _ALIASES.items()} if board.wiring else {}
            lines = {alias: line for alias, line in lines.items() if line is not None}
            valves = {alias: line for alias, line in lines.items() if line.device == REWARD_VALVE}
            unsided = sorted(alias for alias, line in valves.items() if line.side is None)
            if unsided:
                raise ValueError(
                    f"behaviorBoard {name}: RewardValve on {', '.join(unsided)} has no side in wiring {WHERE}. "
                    f"Give it one, as in `{unsided[0]}: {{ device: RewardValve, side: left }}`."
                )
            sides = Counter(line.side.value for line in valves.values() if line.side is not None)
            for side in SIDES:
                if sides[side] > 1:
                    raise ValueError(
                        f"behaviorBoard {name}: wiring has {sides[side]} {side} RewardValves, and a side has one {WHERE}. "
                        f"Keep the line the {side} valve is plugged into, as in `DO1: {{ device: RewardValve, side: {side} }}`, "
                        "and change the others."
                    )
            zapit = [alias for alias, line in lines.items() if line.device == ZAPIT_TRIGGER]
            if len(zapit) > 1:
                raise ValueError(
                    f"behaviorBoard {name}: wiring has {len(zapit)} ZapitTriggers, and a rig has one {WHERE}. "
                    "Keep the line Zapit's trigger is plugged into, as in `DO3: { device: ZapitTrigger }`, and change the others."
                )
            if zapit:
                zapit_boards.append(name)
            if valves:
                valve_boards.append(name)
            outputs = board.pulse_controller.outputs if board.pulse_controller else None
            for alias in sorted(valves):
                output = getattr(outputs, alias.lower(), None) if outputs else None
                if output is None or not isinstance(output.root, CalibratedPulse):
                    raise ValueError(
                        f"behaviorBoard {name}: RewardValve {alias} needs a CalibratedPulse in pulseController.outputs {WHERE}. "
                        f"Add it, as in `{alias}: {{ discriminatorType: CalibratedPulse, artefact: calibration/valve-{alias}.json }}`."
                    )
            # A pulse-enabled line ends itself after its PulseDOx width; the Zapit line (and any masking light on it)
            # is held from the trigger until the workflow clears it.
            pulsed = board.pulse_controller.output_pulse_enable.root.replace(",", " ").split() if board.pulse_controller else []
            if zapit and zapit[0] in pulsed:
                kept = ", ".join(line for line in pulsed if line != zapit[0]) or "None"
                raise ValueError(
                    f"behaviorBoard {name}: ZapitTrigger {zapit[0]} is in pulseController.outputPulseEnable, so the board "
                    f"would end it after PulseDO{zapit[0][-1]} instead of holding it until the workflow clears it {WHERE}. "
                    f'Take {zapit[0]} out, as in `outputPulseEnable: "{kept}"`.'
                )
        if len(zapit_boards) > 1:
            raise ValueError(
                f"behaviorBoard {' and '.join(zapit_boards)} each wire a ZapitTrigger, and a rig has one {WHERE}. "
                "Keep the line Zapit's trigger is plugged into and change the others."
            )
        if len(valve_boards) > 1:
            # DeliverReward and ManualRewards fire every valve through one board's TriggerCalibratedPulse, so a valve on
            # a second board would open on the first board's line of the same number (audit B2, 16 Nov 2026).
            raise ValueError(
                f"behaviorBoard {' and '.join(valve_boards)} each wire a RewardValve, and a rig's reward valves are on one board {WHERE}. "
                "Plug every valve into one board and wire them there."
            )
        if self.zapit is not None and not zapit_boards:
            board = next(iter(self.behavior_board), "main")
            raise ValueError(
                f"rig.yml has zapit, so a board's wiring needs its ZapitTrigger line, and none has one {WHERE}. "
                f"Add it to behaviorBoard.{board}.wiring, as in `DO3: {{ device: ZapitTrigger }}`."
            )
        # Each camera runs on the trigger whose wiring line names it (OpenRigs.Devices CameraTrigger, at Start).
        triggers = Counter(
            target for board in self.behavior_board.values() if board.wiring
            for field, alias in _ALIASES.items()
            if (line := getattr(board.wiring, field)) is not None and line.device == CAMERA_TRIGGER and alias in ("DO0", "DO1")
            for target in (line.targets or []))
        for entry in self.camera:
            name = "Camera-" + entry[:1].upper() + entry[1:]
            if triggers[name] != 1:
                raise ValueError(
                    f"camera {entry} is on {triggers[name]} CameraTrigger lines, and a camera needs exactly one {WHERE}. "
                    f"Name {name} in the targets of the DO0 or DO1 line its trigger cable is on, as in "
                    f"`DO0: {{ device: CameraTrigger, targets: [{name}] }}`."
                )
        return self


_ALIASES ={field: info.alias for field, info in BoardWiring.model_fields.items()}


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


def _placeholders(given: Any, path: str = "") -> Iterator[str]:
    """The key paths of `given` (rig.yml as read) whose value still holds the template's PLACEHOLDER."""
    if isinstance(given, dict):
        for key, value in given.items():
            yield from _placeholders(value, f"{path}{key}.")
    elif isinstance(given, list):
        for index, value in enumerate(given):
            yield from _placeholders(value, f"{path}{index}.")
    elif isinstance(given, str) and PLACEHOLDER in given:
        yield path.rstrip(".")


def _refuse_unknown(keys: list[str]) -> None:
    if keys:
        raise ValueError(
            f"rig.yml has {', '.join(sorted(keys))}, which no lab workflow uses {WHERE}. "
            "The workflow refuses them at Start: delete them from rig.yml."
        )
