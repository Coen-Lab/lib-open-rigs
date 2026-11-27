import math
from typing import Annotated, Any, Literal, Optional

from pydantic import ConfigDict, Field, WithJsonSchema, model_validator

from swc.aeon.schema import BaseSchema

from open_rigs.components.zapit import ZAPIT_ARM_SECONDS, ZAPIT_REPLY_MARGIN_SECONDS
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


class ResponseWindowAlwaysFull(TaskBlock):
    """Whether a choice ends the response window (OpenRigs.Trials TrialTiming.ResponseWindowAlwaysFull), a block a task
    adds beside ResponseWindow; in a task that has it the window always runs in full by default (true): with it, the outcome still comes at the choice with everything it sets off (the reward, the
    spouts, the penalty sound and flash), but the trial runs to the window's end, rewarded or not; the next interval
    starts there, after a penalised trial's timeout, and the drink stays in it (spouts.rewardConsumptionSeconds). Its
    trial log has the trialEnd block (open_rigs.rigs.trials TRIAL_END_COLUMNS)."""

    response_window_always_full: bool = Field(
        default=True,
        description="The response window always runs in full (the default): a choice gives the outcome (reward, spouts, penalty sound and flash) at once, but the trial lasts to the end of the response window, rewarded or not; the next interval starts then, after a penalised trial's timeout, the drink inside it as without this. False: the choice ends the trial. True needs responseWindowSeconds",
    )

    @model_validator(mode="after")
    def _always_full_needs_a_limit(self):
        if self.response_window_always_full and getattr(self, "response_window_seconds", 0) is None:
            raise ValueError("response_window_always_full needs a response_window_seconds: a window with no limit never ends")
        return self


SPOUTS_REWARD_CONSUMPTION_SECONDS = 1.5  # OpenRigs.Trials Spouts.DefaultRewardConsumptionSeconds


class Spouts(TaskBlock):
    """The spouts block of a task whose spouts move (OpenRigs.Trials Spouts and SpoutCommands): both out at trial 1's
    interval start, in at the SpoutsIn cue, the rewarded spout alone at the outcome (else both out), both out at the
    SpoutsOut cue, rewardConsumptionSeconds into the next interval. Which spouts come in is the task's. A task holds it
    as its spouts field."""

    always_in: bool = Field(
        default=False, description="Always in: every spout the task uses is in for the whole trial and interval; they move only at the SpoutsIn cue, never drawn out, and lead nothing (a negative stimulusToSpoutsInSeconds counts as 0)"
    )
    stimulus_to_spouts_in_seconds: float = Field(
        description="From stimulus onset to moving the spouts in (the SpoutsIn cue), in seconds, signed like laserToStimulusSeconds: negative, the spouts in that long before the stimulus (the trial starts that much earlier; with rewardConsumptionSeconds at most itiMinSeconds); positive, at most the stimulus to the response window's opening"
    )
    reward_consumption_seconds: float = Field(
        default=SPOUTS_REWARD_CONSUMPTION_SECONDS,
        ge=0,
        description="From the next interval's start (the outcome plus any timeout) to drawing the spouts out (the SpoutsOut cue), in seconds: time to drink a reward, inside the interval, which it never delays; with any lead of the spouts before the stimulus, at most itiMinSeconds",
    )

    def check_fits(self, iti_min_seconds: float, stimulus_to_response_window_seconds: float) -> None:
        """Refuses spouts drawn out after the shortest interval's stimulus (rewardConsumptionSeconds above iti_min_seconds)
        or after they come back in for it (plus any lead before the stimulus, unless always in), or, with a positive
        stimulusToSpoutsInSeconds, a response window opening before the spouts are in (OpenRigs.Trials Spouts.Problems,
        the same rules). Each task's model calls it with its own values."""
        if self.reward_consumption_seconds > iti_min_seconds + 1e-9:
            raise ValueError(
                f"spouts.reward_consumption_seconds ({self.reward_consumption_seconds} s) is above iti_min_seconds ({iti_min_seconds} s): "
                "the spouts are drawn out reward_consumption_seconds into the interval, before the stimulus"
            )
        lead = 0.0 if self.always_in else max(-self.stimulus_to_spouts_in_seconds, 0.0)
        if self.reward_consumption_seconds + lead > iti_min_seconds + 1e-9:
            raise ValueError(
                f"spouts.reward_consumption_seconds ({self.reward_consumption_seconds} s) plus the {lead} s the spouts come in before "
                f"the stimulus is above iti_min_seconds ({iti_min_seconds} s): the spouts would come in before they are drawn out"
            )
        if self.stimulus_to_spouts_in_seconds > 0 and stimulus_to_response_window_seconds < self.stimulus_to_spouts_in_seconds - 1e-9:
            raise ValueError(
                f"stimulus_to_response_window_seconds ({stimulus_to_response_window_seconds} s) is below spouts.stimulus_to_spouts_in_seconds "
                f"({self.stimulus_to_spouts_in_seconds} s): the response window would open before the spouts arrive"
            )


class StimulusTiming(TaskBlock):
    """When the stimulus ends (OpenRigs.Trials StimulusTiming, TrialTiming.StimulusDurationSeconds and ResponseToStimulusOffSeconds):
    the earlier of its onset plus durationSeconds and the response (the outcome) plus responseToStimulusOffSeconds, logged as
    stimulusOffTime. A task's own stimulus model takes it by inheritance, keeping its keys in the stimulus block."""

    duration_seconds: float | None = Field(
        default=None, gt=0, description="The longest the stimulus lasts from its onset, in seconds; off before the next trial's stimulus can come (the soonest outcome plus the shortest penalty timeout, 0 where a reward can come, plus itiMinSeconds). Null: no cap, the stimulus ends only responseToStimulusOffSeconds after the response (both null is refused)"
    )
    response_to_stimulus_off_seconds: float | None = Field(
        default=None,
        ge=0,
        description="From the response (the outcome; on a miss, the response window's end) to the stimulus off, in seconds, unless the duration ends it first: 0, off at the response; null, the response does not end it (it runs durationSeconds); at most the shortest penalty timeout (0 where a reward can come) plus itiMinSeconds",
    )

    def check_fits(
        self, iti_min_seconds: float, stimulus_to_response_end_seconds: float = math.inf, *, stimulus_to_earliest_outcome_seconds: float, min_outcome_to_iti_seconds: float
    ) -> None:
        """Refuses a stimulus that never ends (both null), one still on at the next trial's, or one past the response
        window's close (OpenRigs.Trials StimulusTiming.Problems, the same rules). The next stimulus comes at the soonest
        stimulus_to_earliest_outcome_seconds (the stimulus to the soonest outcome, the response window's opening; 0 when
        unknown) + min_outcome_to_iti_seconds + iti_min_seconds after this one: min_outcome_to_iti_seconds is the shortest
        outcome to next interval over the outcomes a trial can have (a penalised one's timeout, 0 for one rewarded or
        neither, such as a probe). The next trial's lead moves its start, never its
        stimulus. Each task's model calls it with its own values."""
        after = self.response_to_stimulus_off_seconds
        if self.duration_seconds is None and after is None:
            raise ValueError("stimulus.durationSeconds and stimulus.responseToStimulusOffSeconds are both null: the stimulus would never end")
        if after is not None and after > min_outcome_to_iti_seconds + iti_min_seconds + 1e-9:
            raise ValueError(
                f"stimulus.response_to_stimulus_off_seconds ({after} s) is longer than the shortest outcome to next interval "
                f"({min_outcome_to_iti_seconds} s: the shortest penalty timeout, 0 after a reward) plus iti_min_seconds ({iti_min_seconds} s): the stimulus could still be on at the next trial's"
            )
        if self.duration_seconds is None:
            return
        past = self.duration_seconds - stimulus_to_earliest_outcome_seconds
        if past > min_outcome_to_iti_seconds + iti_min_seconds + 1e-9:
            raise ValueError(
                f"stimulus.duration_seconds ({self.duration_seconds} s) runs {past:g} s past the soonest outcome, longer than the shortest outcome "
                f"to next interval ({min_outcome_to_iti_seconds} s: the shortest penalty timeout, 0 after a reward) plus iti_min_seconds ({iti_min_seconds} s): "
                "the stimulus could still be on at the next trial's"
            )
        if self.duration_seconds > stimulus_to_response_end_seconds + 1e-9:
            raise ValueError(
                f"stimulus.duration_seconds ({self.duration_seconds} s) is longer than the stimulus to the response window's close "
                f"({stimulus_to_response_end_seconds} s)"
            )


class AutoReward(TaskBlock):
    """Auto-reward: the chance a trial is rewarded without a correct response. How it is given is the task's."""

    auto_reward_probability: float = Field(
        ge=0, le=1, description="Probability that a trial is an auto-reward trial, rewarded without a correct response (how is the task's); 0: never, 1: every trial"
    )


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
        """Refuses a laser duration (LaserTiming.ForDuration, or the cap of LaserTiming.UntilStimulusOff; each trial's own: call it on every combination) that leaves Zapit
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

    def check_ramp_down_fits(self, iti_min_seconds: float, ramp_down_seconds: float) -> None:
        """Refuses a ramp-down that leaves Zapit busy at the next trigger when the laser runs until the outcome
        (LaserTiming.UntilOutcome): the trigger line is held the ramp-down past the outcome, and the next trigger comes at the
        soonest iti_min_seconds - laser_to_stimulus_seconds after it, less the reply margin. Off unless enabled.
        ramp_down_seconds is the conditions file's largest (ramp_down_from a validation's context; 0 without it)."""
        if not self.enabled:
            return
        margin = ZAPIT_REPLY_MARGIN_SECONDS
        hold_max = iti_min_seconds - self.laser_to_stimulus_seconds - margin
        if ramp_down_seconds > hold_max + 1e-9:
            raise ValueError(
                f"the Zapit conditions file's largest ramp-down ({ramp_down_seconds:g} s) must be at most {hold_max:g} s "
                f"(trial_structure.iti_min_seconds - optogenetics.laser_to_stimulus_seconds - Zapit's {margin} s reply margin): "
                "the trigger line is held that long past the outcome and the next trial is armed once it falls"
            )


def zapit_trial_seconds(combinations, ramp_down_seconds: float) -> tuple[float, str]:
    """ZapitDaq's per-trial buffer (its MaxTrialSeconds), in seconds, for a session on Zapit-Bonsai that may draw any of
    `combinations`, and why the session cannot run ("" when it can). Each combination is a whole taskParameters, by alias
    as JSON (a trainingStages row or a trial condition over the task's own): every task runs the same trial loop and the
    standard task blocks, so the buffer comes from trialStructure.itiMinSeconds,
    stimulusToResponseWindowSeconds and responseWindowSeconds, and optogenetics.laserToStimulusSeconds alone:

    - a trial's end (OpenRigs.Trials TrialEndSeconds): stimulusToResponseWindowSeconds + responseWindowSeconds (0 without),
      the response window always running in full;
    - the shortest laser trigger to the next trigger: the shortest lead plus trial end, plus the shortest interval less
      the lead, each over the combinations (any may follow any);
    - less OpenRigs.Trials OptoLimits.OverheadSeconds: BusyAfterEndSeconds, the conditions file's largest ramp-down
      (`ramp_down_seconds`), plus ArmLeadSeconds, Zapit's ZAPIT_ARM_SECONDS arm time.

    The same rule as a task model's _between_trials laserDurationSeconds limit. Why not:
    OptoLimits.LeadProblem's words (a combination's lead plus the arm time must fit its own interval), a combination
    lacking a timing field, or no buffer left (0)."""
    leads, ends, itis = [], [], []
    for combination in combinations:
        structure = (combination or {}).get("trialStructure") or {}
        opto = (combination or {}).get("optogenetics") or {}
        values = {"trialStructure.itiMinSeconds": structure.get("itiMinSeconds"),
                  "trialStructure.stimulusToResponseWindowSeconds": structure.get("stimulusToResponseWindowSeconds"),
                  "optogenetics.laserToStimulusSeconds": opto.get("laserToStimulusSeconds")}
        for name, value in values.items():
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                return 0.0, f"{name} is {value!r}, so ZapitDaq's trial buffer cannot be worked out"
        window = structure.get("responseWindowSeconds") or 0.0
        if isinstance(window, bool) or not isinstance(window, (int, float)) or not math.isfinite(window):
            return 0.0, f"trialStructure.responseWindowSeconds is {window!r}, so ZapitDaq's trial buffer cannot be worked out"
        lead, iti = float(values["optogenetics.laserToStimulusSeconds"]), float(values["trialStructure.itiMinSeconds"])
        if lead + ZAPIT_ARM_SECONDS > iti + 1e-9:  # OptoLimits.LeadProblem
            return 0.0, (f"optogenetics.laserToStimulusSeconds ({lead:g} s) plus Zapit's {ZAPIT_ARM_SECONDS:g} s arm time must be at "
                         f"most trialStructure.itiMinSeconds ({iti:g} s), the shortest interval")
        leads.append(lead)
        ends.append(float(values["trialStructure.stimulusToResponseWindowSeconds"]) + float(window))
        itis.append(iti)
    if not leads:
        return 0.0, "the task has no trial combination, so ZapitDaq's trial buffer cannot be worked out"
    gap = min(lead + end for lead, end in zip(leads, ends)) + min(iti - lead for lead, iti in zip(leads, itis))
    overhead = float(ramp_down_seconds) + ZAPIT_ARM_SECONDS  # OptoLimits.OverheadSeconds
    seconds = max(gap - overhead, 0.0)
    if seconds <= 1e-9:
        return 0.0, (f"the shortest laser trigger to the next ({gap:g} s) leaves no ZapitDaq trial buffer after the Zapit "
                     f"conditions file's {float(ramp_down_seconds):g} s ramp-down and Zapit's {ZAPIT_ARM_SECONDS:g} s arm time")
    return seconds, ""


# The lab's sound levels, in dBFS RMS: the RMS of a sound over its samples outside silent stretches (a silent stretch a
# run of exactly-zero samples at least 1 ms long), as the task gives it (before the speaker's equalisation), against full
# scale (OpenRigs.Audio SoundLevel, the same numbers and rule). Defaults only: each task file states its own; they become
# dB SPL once the speakers are calibrated for loudness. The background noise has no task setting: BACKGROUND_LEVEL_DBFS.
STIMULUS_LEVEL_DBFS = -30.0
PENALTY_LEVEL_DBFS = -35.0
PENALTY_TIMEOUT_SECONDS = 4.0  # penalty.incorrect and penalty.noResponse's timeoutSeconds
PENALTY_FLASH_RATE_HZ = 10.0  # penalty.<kind>.flash.rateHz (OpenRigs.Trials PenaltyFlash.DefaultRateHz)
PENALTY_FLASH_MAX_RATE_HZ = 30.0  # half ProjectorDisplay's 60 Hz render loop (PenaltyFlash.MaxRateHz)
BACKGROUND_LEVEL_DBFS = -60.0
PEAK_LIMIT_DBFS = -1.0  # every buffer's peak stays under this after equalisation, checked as the buffer is built
QUIETEST_LEVEL_DBFS = -120.0
MUTE = "mute"  # a level that plays nothing (OpenRigs.Trials SoundLevels.Mute); not `off`, which YAML 1.1 reads as false

# A sound level: dBFS RMS, or "mute". oneOf, not pydantic's anyOf: Bonsai.Sgen makes an empty class of an anyOf item.
SoundLevelDbfs = Annotated[
    Annotated[float, Field(ge=QUIETEST_LEVEL_DBFS, le=PEAK_LIMIT_DBFS)] | Literal["mute"],
    WithJsonSchema({"oneOf": [{"type": "number", "minimum": QUIETEST_LEVEL_DBFS, "maximum": PEAK_LIMIT_DBFS}, {"const": MUTE, "type": "string"}]}),
]


def level_dbfs(default: float | str, sound: str) -> Any:
    """A sound's levelDbfs field, with its lab default."""
    return Field(
        default=default,
        description=f"{sound}'s level in dBFS RMS: the RMS of the sound over its samples outside silent stretches (runs of exactly-zero samples "
        f"at least 1 ms long), as the task gives it, before the speaker's equalisation, against full scale (a full-scale sine is -3); "
        f"{MUTE}: no sound. The lab's default {default if default == MUTE else format(default, 'g')}. The workflow stops at Go if any peak at the highest level a trial can draw would reach "
        f"{PEAK_LIMIT_DBFS:g} dBFS after equalisation. Sounds overlapping on a speaker share its headroom: each buffer's peak is checked, not their mix",
    )


class RewardTone(TaskBlock):
    """A task's reward tone, played at each rewarded trial's levelDbfs (OpenRigs.Audio PlayRewardTone): reward.tone."""

    level_dbfs: SoundLevelDbfs = level_dbfs(MUTE, "The reward tone")


class Reward(TaskBlock):
    """Reward delivery: the reward size, the large-reward trials and its tone. A rewarded trial's next interval starts
    at its outcome; moving spouts go out spouts.rewardConsumptionSeconds into it."""

    size_ul: Double = Field(
        gt=0, le=MAX_REWARD_UL, description=f"Reward size, in microlitres, planned into valve pulses from the valve's calibration; at most {MAX_REWARD_UL}, the most one reward may ask for"
    )
    large_multiplier: Double = Field(default=2.0, gt=1, description="Reward size multiplier on large-reward trials, above 1")
    large_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that a trial is a large-reward trial; 0: none (the only off)")
    tone: RewardTone = Field(default=RewardTone(), description="The reward tone, with every reward")


class PenaltySound(TaskBlock):
    """The lab's penalty sound (OpenRigs.Audio PlayPenaltySound, the same in every task), on rig.yml's speakerArray.penaltySoundChannel alone,
    equalised for that speaker, at each penalised trial's levelDbfs."""

    level_dbfs: SoundLevelDbfs = level_dbfs(PENALTY_LEVEL_DBFS, "The penalty sound")


class PenaltyFlash(TaskBlock):
    """The penalty flash (OpenRigs.Vision PlayPenaltyFlash, the same in every task with a projector): the whole projector
    field at brightness of full white, flashing at rateHz from the outcome for the penalty sound's length, which must fit in
    the kind's timeout (checked at Go against the sound file, OpenRigs.Trials PenaltyFlashRules, the same rules)."""

    brightness: float = Field(
        default=0.0,
        ge=0,
        le=1,
        description="Brightness of the flash, 0 to 1 of the projector's full white after gamma correction (1: full white), over the whole "
        "field and everything drawn, a stimulus still on included; 0: no flash (as the sound's mute). Logged as penaltyFlashBrightness",
    )
    rate_hz: float = Field(
        default=PENALTY_FLASH_RATE_HZ,
        gt=0,
        le=PENALTY_FLASH_MAX_RATE_HZ,
        description=f"Flash rate in Hz: the field is white for the first half of each 1/rateHz cycle and the normal display the other half, "
        f"from the outcome for the penalty sound's length (refused at Go when longer than timeoutSeconds); at most {PENALTY_FLASH_MAX_RATE_HZ:g}, "
        "half the projector's 60 Hz render loop",
    )


class PenaltyCost(TaskBlock):
    """What one kind of penalised outcome costs: its timeout (the trial log's penaltyTimeoutSeconds), the penalty sound and
    the penalty flash (penaltyFlashBrightness)."""

    timeout_seconds: float = Field(default=PENALTY_TIMEOUT_SECONDS, ge=0, description="Timeout after the penalised outcome, in seconds; the next interval starts after it")
    sound: PenaltySound = Field(default=PenaltySound(), description="The penalty sound, with the timeout")
    flash: PenaltyFlash = Field(default=PenaltyFlash(), description="The penalty flash, with the sound; off (brightness 0) by default")


class Penalty(TaskBlock):
    """What a penalised trial costs, by its kind (OpenRigs.Trials Penalties.For): incorrect (a wrong choice, a go/no-go
    false alarm, a probe's tails) or noResponse (a miss), each its own full values, nothing inherited. noResponse null:
    misses are never penalised, and a task that penalises one refuses it at Go (check_misses). Which trials are penalised,
    and of which kind, is the task's."""

    incorrect: PenaltyCost = Field(default=PenaltyCost(), description="What a penalised wrong choice costs (a go/no-go false alarm, a probe's tails)")
    no_response: PenaltyCost | None = Field(
        default=PenaltyCost(), description="What a penalised miss costs; null: misses are never penalised (refused at Go by a task that penalises one)"
    )

    def check_misses(self, misses_penalised: bool) -> None:
        """Refuses noResponse null in a task that penalises a miss (OpenRigs.Trials Penalties.Problems, the same rule)."""
        if misses_penalised and self.no_response is None:
            raise ValueError("penalty.no_response is null, but this task penalises a miss: give penalty.no_response its timeout_seconds and sound")