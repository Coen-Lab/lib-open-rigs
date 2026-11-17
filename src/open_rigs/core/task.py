from typing import Optional

from pydantic import ConfigDict, Field, model_validator

from swc.aeon.schema import BaseSchema

from open_rigs.components.zapit import ZAPIT_REPLY_MARGIN_SECONDS


class TaskParameters(BaseSchema):
    rng_seed: Optional[float] = Field(
        default=None, description="Seed of the random number generator for these task parameters"
    )


class Task(BaseSchema):
    task_parameters: TaskParameters


# The lab's task blocks, one model each so every task names and checks them alike. A task's own
# model takes QuietWait or Optogenetics by inheritance, which keeps its keys where they are in the
# task file, and Penalty as its penalty field.


class TaskBlock(BaseSchema):
    """A block of a task file whose numbers are all finite: YAML's .inf and .nan are refused, so no wait is endless and
    no rule's comparison passes a NaN. Every lab block is one, and so is a task's model built on QuietWait or Optogenetics."""

    model_config = ConfigDict(allow_inf_nan=False)


class QuietWait(TaskBlock):
    """A training stage's quiet waits before a trial's start, counted from the interval's start (OpenRigs.Core QuietWait)."""

    lick_quiet_seconds: float = Field(
        default=0.0,
        ge=0,
        le=10,
        description="Quiet wait: a trial's start needs this long with every spout released, in seconds, counted from the interval's start and restarted by every touch and release; a held contact (a paw or chin on a spout) holds the trial's start until released, so licks for the last reward cannot carry into the next trial; 0: off; at most 10 s. It lengthens the interval only when the mouse licks late in it (quietWaitSeconds)",
    )
    wheel_quiet_seconds: float = Field(
        default=0.0,
        ge=0,
        le=10,
        description="Quiet wait: a trial's start needs this long with the wheel still, in seconds, counted from the interval's start: the wheel's distance (forward and back added) over the last wheelQuietSeconds must stay within wheelQuietCm; 0: off (with wheelQuietCm 0); at most 10 s",
    )
    wheel_quiet_cm: float = Field(
        default=0.0, ge=0, le=20, description="The most the wheel may move, forward and back added, within the last wheelQuietSeconds before the trial's start, in cm; 0: off (with wheelQuietSeconds 0); at most 20 cm"
    )

    @model_validator(mode="after")
    def _wheel_quiet_pair(self):
        if (self.wheel_quiet_seconds > 0) != (self.wheel_quiet_cm > 0):
            raise ValueError("wheel_quiet_seconds and wheel_quiet_cm must both be above 0 (the wheel's quiet wait) or both 0 (off)")
        return self


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
        description="Signed, in seconds: the trigger (the ZapitTrigger line) comes this long before the stimulus; 0 with it; negative after it. The trial's start is the end of the interval less the lead, max(this, 0), once the stage's quiet waits hold, and the stimulus comes the lead after the trial's start in every experiment, optogenetics on or off, so timing never reveals Zapit; the trigger only with optogenetics. Zapit is armed during the interval and adds no time. Refused above itiMinSeconds - 0.2 s (Zapit's reply margin); each task adds its own limits",
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
        self,
        iti_min_seconds: float,
        *,
        trigger_hold_seconds: float | None = None,
        laser_duration_seconds: float | None = None,
        ramp_down_seconds: float = 0.0,
        response_delay_seconds: float = 0.0,
    ) -> None:
        """Refuses laser timing that leaves Zapit busy at the next trial's trigger, less its reply margin (OpenRigs.Devices
        TrialLoop stops there naming the last laser: the same rule). Off unless enabled. A task gives its laser's end,
        one of: trigger_hold_seconds (the laser until the outcome, the line held this long past it: LaserTiming.UntilOutcome),
        or laser_duration_seconds with ramp_down_seconds (LaserTiming.ForDuration) and response_delay_seconds, the soonest
        an outcome follows the stimulus. The next trigger comes at the soonest iti_min_seconds - laser_to_stimulus_seconds
        after an outcome, so (lead + response delay + iti_min - lead) after a trigger."""
        if (trigger_hold_seconds is None) == (laser_duration_seconds is None):
            raise TypeError("check_zapit_free_in_time takes one of trigger_hold_seconds and laser_duration_seconds")
        if not self.enabled:
            return
        iti, lead, margin = iti_min_seconds, self.laser_to_stimulus_seconds, ZAPIT_REPLY_MARGIN_SECONDS
        if trigger_hold_seconds is not None:
            longest = iti - lead - margin
            if trigger_hold_seconds > longest + 1e-9:
                raise ValueError(
                    f"optogenetics.trigger_hold_seconds ({trigger_hold_seconds} s) must be at most {longest:g} s (trial_structure.iti_min_seconds - "
                    f"laser_to_stimulus_seconds - Zapit's {margin} s reply margin): the next trial's StartStimulus goes once the line falls"
                )
            return
        assert laser_duration_seconds is not None  # the other of the two, checked above
        longest = response_delay_seconds + iti - ramp_down_seconds - margin
        if laser_duration_seconds > longest + 1e-9:
            raise ValueError(
                f"optogenetics.laser_duration_seconds ({laser_duration_seconds} s) must be at most {longest:g} s (response_delay_seconds + "
                f"trial_structure.iti_min_seconds - the {ramp_down_seconds} s ramp-down - Zapit's {margin} s reply margin): the next trial's "
                "StartStimulus goes once the laser and its ramp-down are done"
            )


# The lab's sound levels, in dBFS RMS: the RMS of a sound over its whole length as the task gives it (before the
# speaker's equalisation), against full scale (OpenRigs.Audio SoundLevel, the same numbers). Defaults only: each
# task file states its own; they become dB SPL once the speakers are calibrated for loudness.
STIMULUS_LEVEL_DBFS = -30.0
PENALTY_LEVEL_DBFS = -30.0
REWARD_TONE_LEVEL_DBFS = -36.0
BACKGROUND_LEVEL_DBFS = -50.0
PEAK_LIMIT_DBFS = -1.0  # every buffer's peak stays under this after equalisation, checked as the buffer is built
QUIETEST_LEVEL_DBFS = -120.0


def level_dbfs(default: float, sound: str):
    """A sound's levelDbfs field, with its lab default."""
    return Field(
        default=default,
        ge=QUIETEST_LEVEL_DBFS,
        le=PEAK_LIMIT_DBFS,
        description=f"{sound}'s level in dBFS RMS: the RMS of the sound over its whole length as the task gives it, before the speaker's "
        f"equalisation, against full scale (a full-scale sine is -3). The lab's default {default:g}. The workflow stops as the sound's buffer is "
        f"built (at Go for a sound built once, at its trial for one built each trial) if any peak would reach {PEAK_LIMIT_DBFS:g} dBFS after "
        f"equalisation. Sounds overlapping on a speaker share its headroom: each buffer's peak is checked, not their mix",
    )


class StimulusSound(TaskBlock):
    """A task's stimulus sounds, each played at levelDbfs (OpenRigs.Audio SpeakerBuffer or SpeakerSet.Buffer)."""

    level_dbfs: float = level_dbfs(STIMULUS_LEVEL_DBFS, "Each stimulus sound")


class RewardTone(TaskBlock):
    """A task's reward tone, played at levelDbfs."""

    level_dbfs: float = level_dbfs(REWARD_TONE_LEVEL_DBFS, "The reward tone")


class BackgroundNoise(TaskBlock):
    """The background noise, one level for the experiment: looped on every speaker unequalised, faded in over 2 s once
    the task file is read at Go (OpenRigs.Audio SpeakerMixer)."""

    enabled: bool = Field(default=True, description="Whether the background noise plays")
    level_dbfs: float = level_dbfs(BACKGROUND_LEVEL_DBFS, "The background noise")


class PenaltySound(TaskBlock):
    """The lab's penalty sound (OpenRigs.Audio PenaltySoundBuffer, the same in every task), on rig.yml's speakerArray.penaltySoundChannel alone, equalised for that speaker."""

    enabled: bool = Field(default=True, description="Whether the penalty sound plays with every timeout")
    level_dbfs: float = level_dbfs(PENALTY_LEVEL_DBFS, "The penalty sound")


class Penalty(TaskBlock):
    """What a wrong or missed trial costs: a timeout, and the penalty sound with it (the trial log's penaltySoundOn and
    penaltyTimeoutSeconds). Each task says which trials are penalised; timeoutSeconds has no lab default: each task file gives it."""

    timeout_seconds: float = Field(ge=0, description="Timeout after a penalised trial, in seconds; the next interval starts after it")
    sound: PenaltySound = Field(default=PenaltySound(), description="The penalty sound, with every timeout")
