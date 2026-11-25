"""Zapit, a rig's optostimulation, in either of two forms while both run: MATLAB Zapit, the server a rig's tasks arm each
trial over TCP (host, port, hardwareTriggered), and ZapitDaq (OpenRigs.Zapit), the workflow's own NI DAQ driving two
galvos, the laser and the masking LED, with its Basler camera (the native fields, lib_zapit's key names). A blue rig
running both carries both sets in its one zapit block."""
import datetime
import json
import re
from pathlib import Path
from typing import List, Optional

import yaml
from pydantic import Field, field_validator, model_validator
from swc.aeon.schema import BaseSchema

from open_rigs.core.artefacts import ArtefactPath

# How long before its trigger Zapit's armed reply must be in, in seconds (OpenRigs.Devices ZapitTrial.ReplyMarginSeconds):
# every task using Zapit leaves this much between arming Zapit and the trigger.
ZAPIT_REPLY_MARGIN_SECONDS = 0.2
# The most ZapitDaq's Arm may take, in seconds: building the trial's buffer, writing it and starting the task (its Armed
# event's ArmSeconds; OpenRigs.Trials ZapitDaqTrial.ArmSeconds). Every task on ZapitDaq arms at least this long before its
# trigger. To be measured on a rig (a 2.8 s trial at 100 kS/s).
ZAPIT_ARM_SECONDS = 0.2
# How long after a timed ZapitDaq trial's last sample is due the next is armed, in seconds (OpenRigs.Trials
# ZapitDaqTrial.FreeMarginSeconds): the trigger line's edge comes after the loop's trigger tick. The loop frees ZapitDaq at
# the trigger plus the trial's whole length (whole scan cycles and the ramp) plus this, so a laserDurationSeconds limit
# subtracts this, the ramp and one scan cycle.
ZAPIT_FREE_MARGIN_SECONDS = 0.05
# The longest Zapit ramp-down a task may ask for, in seconds (OpenRigs.Devices ZapitConditions.MaximumRampDownSeconds).
ZAPIT_MAX_RAMP_DOWN_SECONDS = 0.5
# The validation context key a task model reads its conditions file's largest ramp-down from (open_rigs.rigs.check_task
# puts it there on the rig, where the file is); without it the workflow checks the file at Go.
RAMP_DOWN_CONTEXT = "zapit_ramp_down_seconds"
SAFETY_LAYERS = ("software", "hardware")


def ramp_down_from(context) -> float:
    """The conditions file's largest ramp-down from a validation's context (a pydantic ValidationInfo, a context dict or
    None), in seconds; 0 without it, the workflow then checking the file at Go."""
    return float((getattr(context, "context", context) or {}).get(RAMP_DOWN_CONTEXT, 0.0))


def max_ramp_down_seconds(path: str | Path) -> float:
    """A Zapit conditions file's highest offRampDownDuration_ms over its stimLocationsNN conditions, in seconds
    (ZapitConditions.Check's MaxRampDownSeconds; the file's other checks are the workflow's, at Go). ValueError for a
    condition with none."""
    doc = yaml.safe_load(Path(path).read_text(encoding="utf-8")) or {}
    conditions = {k: v for k, v in doc.items() if re.fullmatch(r"stimLocations\d+", str(k))}
    missing = [k for k, v in conditions.items() if not isinstance(v, dict) or "offRampDownDuration_ms" not in v]
    if not conditions or missing:
        raise ValueError(f"the Zapit conditions file {path} has {'no conditions' if not conditions else ', '.join(missing) + ' with no offRampDownDuration_ms'}")
    return max(float(v["offRampDownDuration_ms"]) for v in conditions.values()) / 1000


def bank_ramp_down_seconds(bank: str | Path) -> float:
    """A ZapitDaq bank's longest laser ramp-down, in seconds, from its index (lib_zapit's version 2): each laser-on variant's
    tail_cycles scan cycles at the bank's sample rate (OpenRigs.Zapit ZapitBank.LongestRampSeconds, the same rule).
    ValueError for a bank of another version or with no laser-on variant."""
    index = json.loads(Path(bank).read_text(encoding="utf-8"))
    if index.get("version") != 2:
        raise ValueError(f"{bank} is a version {index.get('version')} Zapit bank; version 2 is read")
    rate = float(index["sample_rate"])
    ramps = [v["tail_cycles"] * c["scan_cycle"]["length"] / rate
             for c in index["conditions"] for v in c["variants"] if v["laser_on"]]
    if not ramps:
        raise ValueError(f"{bank} has no laser-on variant")
    return max(ramps)


class SafetyGate(BaseSchema):
    """The laser safety layers this rig was scope-checked with (plan: Safety)."""

    layer: str = Field(
        pattern="^(software|hardware)$",
        description="software: the scope check of the node's finite buffers and zero on stop, and the agent's backstop, passed; hardware: the NI AO watchdog or the OBIS enable line scoped too. The launching app refuses Go on a rig without it.",
    )
    hardware_kind: Optional[str] = Field(default=None, description="The hardware layer built, such as aoWatchdog or obisEnable.")
    date: Optional[str] = Field(default=None, description="The day of the scope check, YYYY-MM-DD.")
    scope_capture: Optional[str] = Field(default=None, description="Where the scope capture is kept.")

    @field_validator("date", mode="before")
    @classmethod
    def _date_as_text(cls, value):
        # An unquoted YAML date loads as a date in Python and as text in the workflow's reader.
        return value.isoformat() if isinstance(value, datetime.date) else value


class ZapitCamera(BaseSchema):
    """Zapit's Basler camera, opened with stock Bonsai.Pylon PylonCapture from a camera settings (.pfs) file. Its frames
    are paired with the Behavior board camera trigger whose wiring line names Camera-Zapit in targets."""

    serial_number: str = Field(min_length=1, description="The Basler camera's serial number.")
    session_pfs_path: ArtefactPath = Field(
        default="calibration/Zapit-Session.pfs",
        description="The session's camera settings (hardware triggered, ROI, ExposureAuto), relative to rig.yml's folder, from the camera set-up calibration.",
    )
    calibration_pfs_path: ArtefactPath = Field(
        default="calibration/Zapit-Calibration.pfs",
        description="The calibration workflow's camera settings (fixed exposure, free running), relative to rig.yml's folder.",
    )


# The native fields a block naming its NI device (zapit.device) must all give.
ZAPIT_DAQ_REQUIRED = ("device", "galvo_x_channel", "galvo_y_channel", "laser_channel", "led_channel", "photodiode_channel",
                      "trigger_channel", "sample_rate", "laser_min_max_mw", "laser_min_max_control_volts", "ceiling_mw",
                      "max_trial_seconds", "early_stop_lead_seconds", "microns_per_pixel", "volts_per_pixel", "safety_gate", "camera")

# The trigger filter's widest minimum pulse: the trigger is a level held about 1 s, so a wider filter would start eating it.
ZAPIT_TRIGGER_FILTER_LIMIT_SECONDS = 0.01


class Zapit(BaseSchema):
    """Zapit on this rig: MATLAB Zapit's TCP endpoint (host, port, hardwareTriggered: the tcpServer section of its settings
    file; the trigger line is the board's ZapitTrigger in wiring), ZapitDaq's NI DAQ wiring, laser limits and camera (the
    native fields, all given once device is), or both while a rig runs both. A channel is named on its own (ao0, PFI0)
    beside the device; OpenRigs.Devices Rigs.Zapit gives the full paths ZapitDaq takes (Dev1/ao0, /Dev1/PFI0)."""

    host: str = Field(default="127.0.0.1", description="Host name or IP address of the PC running Zapit.")
    port: int = Field(default=1488, ge=1, le=65535, description="TCP port the Zapit server listens on.")
    hardware_triggered: bool = Field(
        default=False,
        description="True: Zapit arms its DAQ on the TCP command and the ZapitTrigger line starts the stimulus. False: Zapit starts the stimulus on receipt of the command.",
    )
    device: Optional[str] = Field(default=None, pattern=r"^\S+$", examples=["Dev1"], description="ZapitDaq's NI device, as NI MAX names it; with it, every native field below is given.")
    galvo_x_channel: Optional[str] = Field(default=None, pattern=r"^ao\d+$", examples=["ao0"], description="The AO line driving the X galvo.")
    galvo_y_channel: Optional[str] = Field(default=None, pattern=r"^ao\d+$", examples=["ao1"], description="The AO line driving the Y galvo.")
    laser_channel: Optional[str] = Field(default=None, pattern=r"^ao\d+$", examples=["ao2"], description="The AO line setting the laser's power (its control volts).")
    led_channel: Optional[str] = Field(default=None, pattern=r"^ao\d+$", examples=["ao3"], description="The AO line driving the masking LED.")
    photodiode_channel: Optional[str] = Field(default=None, pattern=r"^ai\d+$", examples=["ai0"], description="The AI line reading the photodiode, for the laser curve.")
    blanking_times_ms: Optional[List[float]] = Field(default=None, min_length=3, max_length=3, description="The laser blanking time, onset shift and offset shift, in ms, as lib_zapit reads them. Absent, lib_zapit uses MATLAB's 0.3, -0.12, 0.25. Optional, and not part of the native fields a block gives together.")
    trigger_channel: Optional[str] = Field(default=None, pattern=r"^PFI\d+$", examples=["PFI0"], description="The PFI line the Behavior board's ZapitTrigger line is cabled to; each trial starts on it.")
    sample_rate: Optional[float] = Field(default=None, gt=0, examples=[100000], description="AO samples a second, per channel; every bank is built at it.")
    laser_min_max_mw: Optional[List[float]] = Field(default=None, min_length=2, max_length=2, description="The laser's lowest and highest power, in mW, at laserMinMaxControlVolts.")
    laser_min_max_control_volts: Optional[List[float]] = Field(default=None, min_length=2, max_length=2, description="The laser's control volts at its lowest and highest power.")
    ceiling_mw: Optional[float] = Field(default=None, ge=0, description="This rig's laser ceiling, a peak power in mW (a task's laserPowerMw is time-averaged per point, below its peak): lib_zapit caps every volts value it hands out (bank, aiming, scanner calibration) at it. 0 means the laser is disabled: every laser volts value is 0 V.")
    max_trial_seconds: Optional[float] = Field(default=None, gt=0, description="The longest trial buffer, in seconds: a task's laserCapSeconds above it is refused at Go, and a trial until the outcome is finite at it.")
    early_stop_lead_seconds: Optional[float] = Field(default=None, gt=0, description="How far ahead of the samples generated an early stop writes its ramp, in seconds: more than the DAQ's onboard buffer plus the bank's longest scan cycle, which ZapitDaq checks at the first trial armed with no duration. The lab's is 0.08 (a USB-6453's 4095 samples per channel and a 40 Hz cycle at 100000 samples/s need above 0.06595).")
    trigger_filter_seconds: float = Field(
        default=0.001, ge=0, lt=ZAPIT_TRIGGER_FILTER_LIMIT_SECONDS,
        description="Minimum pulse width of the NI card's digital filter on the trigger line (triggerChannel), in seconds: an edge pair closer than this is a glitch and does not start a trial. The filter delays the recognised edge by up to this width; the trigger is a level held about 1 s, so the default 1 ms rejects glitches without missing triggers. 0 turns the filter off. Optional, and not part of the native fields a block gives together.",
    )
    microns_per_pixel: Optional[float] = Field(default=None, gt=0, description="The Zapit camera's scale at the sample, in microns a pixel.")
    volts_per_pixel: Optional[float] = Field(default=None, gt=0, description="The galvos' volts a camera pixel, the scanner calibration's starting guess.")
    invert_x_scanner: bool = Field(default=False, description="Whether the X galvo moves against the camera's X.")
    invert_y_scanner: bool = Field(default=False, description="Whether the Y galvo moves against the camera's Y.")
    safety_gate: Optional[SafetyGate] = Field(default=None, description="The laser safety layers this rig was scope-checked with.")
    camera: Optional[ZapitCamera] = Field(default=None, description="Zapit's Basler camera, Camera-Zapit in the data and live view.")

    @property
    def has_daq(self) -> bool:
        """Whether the block gives ZapitDaq's fields (zapit.device), not MATLAB Zapit's alone."""
        return self.device is not None

    @model_validator(mode="after")
    def _native_fields_together(self) -> "Zapit":
        given = [name for name in ZAPIT_DAQ_REQUIRED if getattr(self, name) is not None]
        if given and len(given) < len(ZAPIT_DAQ_REQUIRED):
            fields = type(self).model_fields
            missing = [fields[name].alias or name for name in ZAPIT_DAQ_REQUIRED if getattr(self, name) is None]
            raise ValueError(f"zapit gives some of ZapitDaq's fields ({', '.join(fields[n].alias or n for n in given)}) but not {', '.join(missing)}: "
                             "a block with ZapitDaq's gives them all (MATLAB Zapit's host, port and hardwareTriggered alone give none)")
        for name, pair in (("laserMinMaxMw", self.laser_min_max_mw), ("laserMinMaxControlVolts", self.laser_min_max_control_volts)):
            if pair is not None and not pair[1] > pair[0]:
                raise ValueError(f"zapit.{name} must rise from the first value to the second, not {pair}")
        return self
