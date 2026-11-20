from typing import Annotated, Literal, Optional

from pydantic import ConfigDict, Field, WithJsonSchema, model_validator

from swc.aeon.schema import BaseSchema

from open_rigs.components.zapit import ZAPIT_REPLY_MARGIN_SECONDS
from open_rigs.core.base import Double

# The most one reward may ask for, in ul (OpenRigs.Core LoadRewardLine.DefaultMaxRewardUl, a RewardValve piece's MaxRewardUl).
MAX_REWARD_UL = 20


class TaskParameters(BaseSchema):
    rng_seed: Optional[float] = Field(
        default=None, description="Seed of the random number generator for these task parameters"
    )


class Task(BaseSchema):
    task_parameters: TaskParameters


# The lab's task blocks, one model each so every task names and checks them alike. A task's own
# model takes LickQuietWait, WheelQuietWait, Iti, ResponseWindow, AutoReward or Optogenetics by inheritance,
# which keeps its keys where they are in the task file, and Penalty and Reward as its penalty and reward fields. A setting sits on the training
# stage rows only if some stage of the task changes it; otherwise it is a task-level setting, under the same name.


class TaskBlock(BaseSchema):
    """A block of a task file whose numbers are all finite: YAML's .inf and .nan are refused, so no wait is endless and
    no rule's comparison passes a NaN. Every lab block is one, and so is a task's model built on one."""

    model_config = ConfigDict(allow_inf_nan=False)


class LickQuietWait(TaskBlock):
    """The lick quiet wait before a trial's start, counted from the interval's start (OpenRigs.Core QuietWait). A task puts
    it on its training stages if a stage changes it, else at task level; WheelQuietWait likewise."""

    lick_quiet_seconds: float = Field(
        default=0.0,
        ge=0,
        le=10,
        description="Quiet wait: a trial's start needs this long with every spout released, in seconds, counted from the interval's start and restarted by every touch and release; a held contact (a paw or chin on a spout) holds the trial's start until released, so licks for the last reward cannot carry into the next trial; 0: off; at most 10 s. It lengthens the interval only when the mouse licks late in it (quietWaitSeconds)",
    )


class WheelQuietWait(TaskBlock):
    """The wheel quiet wait before a trial's start, counted from the interval's start (OpenRigs.Core QuietWait)."""

    wheel_quiet_seconds: float = Field(
        default=0.0,
        ge=0,
        le=10,
        description="Quiet wait: a trial's start needs this long with the wheel still, in seconds, counted from the interval's start: the wheel's distance (forward and back added) over the last wheelQuietSeconds must stay within wheelQuietCm; 0: off (the only off); at most 10 s",
    )
    wheel_quiet_cm: float = Field(
        default=0.0, ge=0, le=20, description="The most the wheel may move, forward and back added, within the last wheelQuietSeconds before the trial's start, in cm; 0: no movement at all; at most 20 cm"
    )


class Iti(TaskBlock):
    """The inter-trial interval, drawn uniformly for every trial between itiMinSeconds and itiMaxSeconds. Where it starts
    and ends is the task's, said in its own description. No lab default: each task file gives both."""

    iti_min_seconds: float = Field(
        ge=0, description="Lower bound of the inter-trial interval, drawn uniformly for every trial, in seconds; the quiet waits can only lengthen it (quietWaitSeconds)"
    )
    iti_max_seconds: float = Field(ge=0, description="Upper bound of the inter-trial interval, in seconds; at least itiMinSeconds")

    @model_validator(mode="after")
    def _iti_ordered(self):
        if self.iti_max_seconds < self.iti_min_seconds:
            raise ValueError("iti_max_seconds must be >= iti_min_seconds")
        return self


class ResponseWindow(TaskBlock):
    """The response window: how long a trial waits for the mouse's response, from when a lick first counts (each task
    says when that is)."""

    response_window_seconds: float | None = Field(
        default=None,
        gt=0,
        description="The response window, in seconds from when a lick first counts (the task says when); with no response by its end the trial is noResponse. Null or left out: no limit (a task may refuse it)",
    )


SPOUTS_REWARD_CONSUMPTION_SECONDS = 1.5  # OpenRigs.Trials Spouts.DefaultRewardConsumptionSeconds


class Spouts(TaskBlock):
    """The spouts block of a task whose spouts move (OpenRigs.Trials Spouts and SpoutCommands): both out at trial 1's
    interval start, in at the SpoutsIn cue, the rewarded spout alone at the outcome (else both out), both out at the
    SpoutsOut cue. Which spouts come in is the task's. A task holds it as its spouts field."""

    stay_in: bool = Field(
        default=False, description="Whether the trial's spouts stay in: they move only at the SpoutsIn cue, never drawn out"
    )
    stimulus_to_in_seconds: float = Field(
        ge=0, description="From stimulus onset to moving the spouts in (the SpoutsIn cue), in seconds; at most the stimulus to the response window's opening"
    )
    reward_consumption_seconds: float = Field(
        default=SPOUTS_REWARD_CONSUMPTION_SECONDS,
        ge=0,
        description="From the next interval's start (the outcome plus any timeout) to drawing the spouts out (the SpoutsOut cue), in seconds: time to drink a reward; at most itiMinSeconds",
    )

    def check_fits(self, iti_min_seconds: float, stimulus_to_response_window_seconds: float) -> None:
        """Refuses spouts drawn out after the shortest interval's stimulus, or a response window opening before the spouts
        are in (OpenRigs.Trials Spouts.Problems, the same rules). Each task's model calls it with its own values."""
        if self.reward_consumption_seconds > iti_min_seconds + 1e-9:
            raise ValueError(
                f"spouts.reward_consumption_seconds ({self.reward_consumption_seconds} s) is above iti_min_seconds ({iti_min_seconds} s): "
                "the spouts are drawn out reward_consumption_seconds into the interval, before the stimulus"
            )
        if stimulus_to_response_window_seconds < self.stimulus_to_in_seconds - 1e-9:
            raise ValueError(
                f"stimulus_to_response_window_seconds ({stimulus_to_response_window_seconds} s) is below spouts.stimulus_to_in_seconds "
                f"({self.stimulus_to_in_seconds} s): the response window would open before the spouts arrive"
            )


class AutoReward(TaskBlock):
    """Auto-reward: the chance a trial is rewarded without a correct response. How it is given is the task's."""

    auto_reward_probability: float = Field(
        ge=0, le=1, description="Probability that a trial is an auto-reward trial, rewarded without a correct response (how is the task's); 0: never, 1: every trial"
    )


class Reward(TaskBlock):
    """Reward delivery: the reward size and the large-reward trials."""

    size_ul: Double = Field(
        gt=0, le=MAX_REWARD_UL, description=f"Reward size, in microlitres, planned into valve pulses from the valve's calibration; at most {MAX_REWARD_UL}, the most one reward may ask for"
    )
    large_multiplier: Double = Field(default=2.0, gt=1, description="Reward size multiplier on large-reward trials, above 1")
    large_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that a trial is a large-reward trial; 0: none (the only off)")


class Optogenetics(TaskBlock):
    """Zapit optogenetic stimulation: what every task's optogenetics block holds. A task adds its own laser timing.

    Every trial of an optogenetics experiment sends Zapit a condition from the conditions file loaded in Zapit, laser-on
    trials with the laser on and the others with it off, so galvo noise and masking light are the same on every trial.
    Zapit's host, port and triggering are rig.yml's zapit block. laserOnProbability, laserToStimulusSeconds and
    trialsBeforeLaser have no lab default: each task file gives them.
    """

    # The same rules as _power_when_on's, in the JSON schema: the launching app checks the task file against the
    # schema, not this model, so a ticked Zapit with no conditions file was sent at Go and the workflow
    # stopped at trial 1. additionalProperties is spelt out because the launching app closes
    # every object that lists properties.
    model_config = ConfigDict(
        json_schema_extra={
            "if": {"properties": {"enabled": {"const": True}}, "required": ["enabled"], "additionalProperties": True},
            "then": {
                "properties": {"zapitConditionsPath": {"minLength": 1}, "laserPowerMw": {"type": "number"}},
                "required": ["laserPowerMw"],
                "additionalProperties": True,
            },
        }
    )

    enabled: bool = Field(
        default=False,
        description="Whether this is an optogenetics experiment, Zapit running it: every trial sends it a condition from the conditions file (laser off at 0 mW on laser-off trials: the galvos move and the masking light shows), and the launching app checks Zapit is answering before Go",
    )
    zapit_conditions_path: str = Field(
        default="",
        description="Path to the Zapit conditions file loaded in Zapit. Its top-level stimLocations01, stimLocations02, ... entries are the conditions, numbered 1 to N with no gaps. The launching app keeps a copy beside the task file at Go",
    )
    laser_power_mw: float | None = Field(
        default=None,
        gt=0,
        description="Laser power on laser-on trials, in mW, the same for every condition: sent to Zapit with each StartStimulus (overriding the conditions file's power; Zapit's laser calibration turns it into volts) and logged as laserPowerMw (0 on laser-off trials). Required with optogenetics on",
    )
    laser_on_probability: float = Field(
        ge=0,
        le=1,
        description="Probability that a trial after trialsBeforeLaser is a laser-on trial, shared equally by every condition in the conditions file; all other trials are laser-off trials, sent a condition with the laser off",
    )
    laser_to_stimulus_seconds: float = Field(
        description="Signed, in seconds: the trigger (the ZapitTrigger line) comes this long before the stimulus; 0 with it; negative after it. The trial's start is the end of the interval less the lead, max(this, 0), once the quiet waits hold, and the stimulus comes the lead after the trial's start in every experiment, optogenetics on or off, so timing never reveals Zapit; the trigger only with optogenetics. Zapit is armed during the interval and adds no time. Refused above itiMinSeconds - 0.2 s (Zapit's reply margin); each task adds its own limits",
    )
    trials_before_laser: int = Field(
        ge=0, description="Trials at the experiment's start with no laser-on trial among them (each still sends Zapit a condition, at 0 mW); laserOnProbability applies from the next trial"
    )

    @model_validator(mode="after")
    def _power_when_on(self):
        if self.enabled and not self.zapit_conditions_path:
            raise ValueError("optogenetics is enabled but zapit_conditions_path is empty")
        if self.enabled and self.laser_power_mw is None:
            raise ValueError("optogenetics is enabled but laser_power_mw is not set")
        return self

    def check_lead_fits(self, iti_min_seconds: float) -> None:
        """Refuses a lead that, with Zapit's reply margin, does not fit in the shortest interval (OpenRigs.Devices
        ZapitTrial.LeadProblem, the same rule). Each task's model calls it with its trial_structure.iti_min_seconds."""
        lead = self.laser_to_stimulus_seconds
        if lead + ZAPIT_REPLY_MARGIN_SECONDS > iti_min_seconds + 1e-9:
            raise ValueError(
                f"optogenetics.laser_to_stimulus_seconds ({lead} s) plus Zapit's {ZAPIT_REPLY_MARGIN_SECONDS} s reply margin must be at most "
                f"trial_structure.iti_min_seconds ({iti_min_seconds} s), the shortest interval"
            )

    def check_zapit_free_in_time(
        self, iti_min_seconds: float, *, laser_duration_seconds: float, ramp_down_seconds: float = 0.0, response_delay_seconds: float = 0.0
    ) -> None:
        """Refuses a laser duration (LaserTiming.ForDuration, each trial's own: call it on every combination) that leaves Zapit
        busy at the next trial's trigger, less its reply margin (OpenRigs.Trials TrialLoop stops there naming the last laser: the
        same rule). Off unless enabled. response_delay_seconds is the soonest an outcome follows the stimulus; the next trigger
        comes at the soonest iti_min_seconds - laser_to_stimulus_seconds after an outcome. A laser until the outcome holds the
        line for the conditions file's MaxRampDownSeconds (ZapitConditions.Check), read at connect, so the workflow checks it."""
        if not self.enabled:
            return
        margin = ZAPIT_REPLY_MARGIN_SECONDS
        longest = response_delay_seconds + iti_min_seconds - ramp_down_seconds - margin
        if laser_duration_seconds > longest + 1e-9:
            raise ValueError(
                f"optogenetics.laser_duration_seconds ({laser_duration_seconds} s) must be at most {longest:g} s (response_delay_seconds + "
                f"trial_structure.iti_min_seconds - the {ramp_down_seconds} s ramp-down - Zapit's {margin} s reply margin): the next trial's "
                "StartStimulus goes once the laser and its ramp-down are done"
            )


# The lab's sound levels, in dBFS RMS: the RMS of a sound over its samples outside silent stretches (a silent stretch a
# run of exactly-zero samples at least 1 ms long), as the task gives it (before the speaker's equalisation), against full
# scale (OpenRigs.Audio SoundLevel, the same numbers and rule). Defaults only: each task file states its own; they become
# dB SPL once the speakers are calibrated for loudness. The background noise has no task setting: BACKGROUND_LEVEL_DBFS.
STIMULUS_LEVEL_DBFS = -30.0
PENALTY_LEVEL_DBFS = -35.0
REWARD_TONE_LEVEL_DBFS = -35.0
BACKGROUND_LEVEL_DBFS = -60.0
PEAK_LIMIT_DBFS = -1.0  # every buffer's peak stays under this after equalisation, checked as the buffer is built
QUIETEST_LEVEL_DBFS = -120.0
MUTE = "mute"  # a level that plays nothing (OpenRigs.Trials SoundLevels.Mute); not `off`, which YAML 1.1 reads as false

# A sound level: dBFS RMS, or "mute". oneOf, not pydantic's anyOf: Bonsai.Sgen makes an empty class of an anyOf item.
SoundLevelDbfs = Annotated[
    Annotated[float, Field(ge=QUIETEST_LEVEL_DBFS, le=PEAK_LIMIT_DBFS)] | Literal["mute"],
    WithJsonSchema({"oneOf": [{"type": "number", "minimum": QUIETEST_LEVEL_DBFS, "maximum": PEAK_LIMIT_DBFS}, {"const": MUTE, "type": "string"}]}),
]
# A sound level that may be null, still one oneOf.
NullableSoundLevelDbfs = Annotated[
    Annotated[float, Field(ge=QUIETEST_LEVEL_DBFS, le=PEAK_LIMIT_DBFS)] | Literal["mute"] | None,
    WithJsonSchema({"oneOf": [{"type": "number", "minimum": QUIETEST_LEVEL_DBFS, "maximum": PEAK_LIMIT_DBFS}, {"const": MUTE, "type": "string"}, {"type": "null"}]}),
]


def level_dbfs(default: float, sound: str):
    """A sound's levelDbfs field, with its lab default."""
    return Field(
        default=default,
        description=f"{sound}'s level in dBFS RMS: the RMS of the sound over its samples outside silent stretches (runs of exactly-zero samples "
        f"at least 1 ms long), as the task gives it, before the speaker's equalisation, against full scale (a full-scale sine is -3); "
        f"{MUTE}: no sound. The lab's default {default:g}. The workflow stops at Go if any peak at the highest level a trial can draw would reach "
        f"{PEAK_LIMIT_DBFS:g} dBFS after equalisation. Sounds overlapping on a speaker share its headroom: each buffer's peak is checked, not their mix",
    )


class RewardTone(TaskBlock):
    """A task's reward tone, played at each trial's levelDbfs (OpenRigs.Audio PlayRewardTone)."""

    level_dbfs: SoundLevelDbfs = level_dbfs(REWARD_TONE_LEVEL_DBFS, "The reward tone")


class PenaltySound(TaskBlock):
    """The lab's penalty sound (OpenRigs.Audio PlayPenaltySound, the same in every task), on rig.yml's speakerArray.penaltySoundChannel alone,
    equalised for that speaker, at each penalised trial's levelDbfs."""

    level_dbfs: SoundLevelDbfs = level_dbfs(PENALTY_LEVEL_DBFS, "The penalty sound")


class PenaltyCostSound(TaskBlock):
    """A penalised miss's penalty sound: its level, or null for the penalised choice's (penalty.sound.levelDbfs)."""

    level_dbfs: NullableSoundLevelDbfs = Field(
        default=None,
        description=f"The penalty sound's level on a penalised miss, in dBFS RMS as penalty.sound.levelDbfs; {MUTE}: no sound; null: penalty.sound.levelDbfs",
    )


class PenaltyCost(TaskBlock):
    """What a penalised miss costs, each field null for the penalised choice's (penalty's own): never null itself, so a
    condition or a training stage can set any one of its fields by its dotted path."""

    timeout_seconds: float | None = Field(
        default=None, ge=0, description="Timeout after a penalised miss, in seconds; null: penalty.timeoutSeconds"
    )
    sound: PenaltyCostSound = Field(default=PenaltyCostSound(), description="The penalty sound on a penalised miss")


class Penalty(TaskBlock):
    """What a penalised trial costs: a timeout (the trial log's penaltyTimeoutSeconds), and the penalty sound with it
    unless its level is mute. A penalised choice costs this block's own values, a penalised miss its noResponse override
    (a null field the choice's). Each task decides which trials are penalised and which are misses; timeoutSeconds has no
    lab default: each task file gives it."""

    timeout_seconds: float = Field(ge=0, description="Timeout after a penalised choice, in seconds, and after a penalised miss unless noResponse.timeoutSeconds sets its own; the next interval starts after it")
    sound: PenaltySound = Field(default=PenaltySound(), description="The penalty sound, with every timeout")
    no_response: PenaltyCost = Field(
        default=PenaltyCost(), description="What a penalised miss costs where it differs from a penalised choice: each null field the choice's"
    )
