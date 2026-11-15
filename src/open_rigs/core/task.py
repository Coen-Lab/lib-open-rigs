import math
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


class QuietWait(BaseSchema):
    """A training stage's quiet waits before Go, counted from the interval's start (OpenRigs.Core QuietWait)."""

    lick_quiet_seconds: float = Field(
        default=0.0,
        ge=0,
        description="Quiet wait: Go needs this long with every spout released, in seconds, counted from the interval's start and restarted by every touch and release; a held contact (a paw or chin on a spout) holds Go until released, so licks for the last reward cannot carry into the next trial; 0: off. It lengthens the interval only when the mouse licks late in it (quietWaitSeconds)",
    )
    wheel_quiet_seconds: float = Field(
        default=0.0,
        ge=0,
        description="Quiet wait: Go needs this long with the wheel still, in seconds, counted from the interval's start: the wheel's distance (forward and back added) over the last wheelQuietSeconds must stay within wheelQuietCm; 0: off (with wheelQuietCm 0)",
    )
    wheel_quiet_cm: float = Field(
        default=0.0, ge=0, description="The most the wheel may move, forward and back added, within the last wheelQuietSeconds before Go, in cm; 0: off (with wheelQuietSeconds 0)"
    )

    @model_validator(mode="after")
    def _wheel_quiet_pair(self):
        if (self.wheel_quiet_seconds > 0) != (self.wheel_quiet_cm > 0):
            raise ValueError("wheel_quiet_seconds and wheel_quiet_cm must both be above 0 (the wheel's quiet wait) or both 0 (off)")
        return self


class Optogenetics(BaseSchema):
    """Zapit optogenetic stimulation: what every task's optogenetics block holds. A task adds its own laser timing.

    Every trial of an optogenetics experiment sends Zapit a condition from the conditions file loaded in Zapit, laser-on
    trials with the laser on and the others with it off, so galvo noise and masking light are the same on every trial.
    Zapit's host, port and triggering are rig.yml's zapit block. laserOnProbability, laserToStimulusSeconds and
    trialsBeforeLaser have no lab default: each task file gives them.
    """

    # The same rules as _power_when_on's, in the JSON schema: the launching app checks the task file against the
    # schema, not this model, so a ticked Zapit with no conditions file was sent at Go and the workflow
    # stopped at trial 1 (audit, 9 Nov 2026). additionalProperties is spelt out because the launching app closes
    # every object that lists properties.
    model_config = ConfigDict(
        json_schema_extra={
            "if": {"properties": {"useZapit": {"const": True}}, "required": ["useZapit"], "additionalProperties": True},
            "then": {
                "properties": {"zapitConfigPath": {"minLength": 1}, "laserPowerMw": {"type": "number"}},
                "required": ["laserPowerMw"],
                "additionalProperties": True,
            },
        }
    )

    use_zapit: bool = Field(
        default=False,
        description="Whether Zapit runs this experiment: every trial sends it a condition from the conditions file (laser off at 0 mW on laser-off trials: the galvos move and the masking light shows), and the launching app checks Zapit is answering before Go",
    )
    zapit_config_path: str = Field(
        default="",
        description="Path to the Zapit conditions file loaded in Zapit. Its top-level stimLocations01, stimLocations02, ... entries are the conditions, numbered 1 to N with no gaps. Named ...ConfigPath so the launching app keeps a copy beside the task file at Go",
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
        description="Signed, in seconds: the trigger (the ZapitTrigger line) comes this long before the stimulus; 0 with it; negative after it. Go is the end of the interval less the lead, max(this, 0), once the stage's quiet waits hold, and the stimulus comes the lead after Go in every experiment, optogenetics on or off, so timing never reveals Zapit; the trigger only with optogenetics. Zapit is armed during the interval and adds no time. Refused above itiMinSeconds - 0.2 s (Zapit's reply margin); each task adds its own limits",
    )
    trials_before_laser: int = Field(
        ge=0, description="Trials at the experiment's start with no laser-on trial among them (each still sends Zapit a condition, at 0 mW); laserOnProbability applies from the next trial"
    )

    @model_validator(mode="after")
    def _power_when_on(self):
        if self.use_zapit and not self.zapit_config_path:
            raise ValueError("use_zapit is on but zapit_config_path is empty")
        if self.use_zapit and self.laser_power_mw is None:
            raise ValueError("use_zapit is on but laser_power_mw is not set")
        if self.laser_power_mw is not None and not math.isfinite(self.laser_power_mw):
            raise ValueError(f"laser_power_mw ({self.laser_power_mw}) must be a finite number of mW")
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


class PenaltySound(BaseSchema):
    """The lab's penalty sound (OpenRigs.Audio PenaltySoundBuffer, the same in every task), on rig.yml's speakerArray.penaltySoundChannel alone, equalised for that speaker."""

    enabled: bool = Field(default=True, description="Whether the penalty sound plays with every timeout")
    gain: float = Field(default=1.0, ge=0, description="Gain on the lab's penalty sound, 1 for its level")


class Penalty(BaseSchema):
    """What a wrong or missed trial costs: a timeout, and the penalty sound with it (the trial log's penaltySound and
    penaltyTimeoutSeconds). Each task says which trials are penalised; timeoutSeconds has no lab default: each task file gives it."""

    timeout_seconds: float = Field(ge=0, description="Timeout after a penalised trial, in seconds; the next interval starts after it")
    sound: PenaltySound = Field(default=PenaltySound(), description="The penalty sound, with every timeout")
