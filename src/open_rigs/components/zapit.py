"""Zapit, the rig's optostimulation: an NI DAQ driving two galvos, the laser and the masking LED (OpenRigs.Zapit ZapitDaq),
and its Basler camera (Bonsai.Pylon). Wiring and limits only: calibrations, the sample and the bank live elsewhere. The
key names are lib_zapit's (rig_config.ZapitRig, which reads the same block in Python)."""
import datetime
import json
from pathlib import Path
from typing import List, Optional

from pydantic import Field, field_validator, model_validator
from swc.aeon.schema import BaseSchema

from open_rigs.core.artefacts import ArtefactPath

# The most ZapitDaq's Arm may take, in seconds: building the trial's buffer, writing it and starting the task (its Armed
# event's ArmSeconds; OpenRigs.Trials ZapitTrial.ArmSeconds). Every task using Zapit arms at least this long before its
# trigger. To be measured on a rig (a 2.8 s trial at 100 kS/s).
ZAPIT_ARM_SECONDS = 0.2
# The validation context key a task model reads the bank's longest ramp-down from (open_rigs.rigs.check_task puts it
# there from the session's bank, Settings_ZapitBank_<DateTime>.json); without it the workflow checks at Go.
RAMP_DOWN_CONTEXT = "zapit_ramp_down_seconds"
SAFETY_LAYERS = ("software", "hardware")


def ramp_down_from(context) -> float:
    """The bank's longest ramp-down from a validation's context (a pydantic ValidationInfo, a context dict or None), in
    seconds; 0 without it, the workflow then checking at Go."""
    return float((getattr(context, "context", context) or {}).get(RAMP_DOWN_CONTEXT, 0.0))


def max_ramp_down_seconds(bank: str | Path) -> float:
    """A Zapit bank's longest laser ramp-down, in seconds, from its index (lib_zapit's version 2): each laser-on variant's
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


class Zapit(BaseSchema):
    """Zapit's NI DAQ wiring and laser limits. A channel is named on its own (ao0, PFI0) beside the device; OpenRigs.Devices
    Rigs.Zapit gives the full paths ZapitDaq takes (Dev1/ao0, /Dev1/PFI0)."""

    device: str = Field(pattern=r"^\S+$", examples=["Dev1"], description="The NI device, as NI MAX names it.")
    galvo_x_channel: str = Field(pattern=r"^ao\d+$", examples=["ao0"], description="The AO line driving the X galvo.")
    galvo_y_channel: str = Field(pattern=r"^ao\d+$", examples=["ao1"], description="The AO line driving the Y galvo.")
    laser_channel: str = Field(pattern=r"^ao\d+$", examples=["ao2"], description="The AO line setting the laser's power (its control volts).")
    led_channel: str = Field(pattern=r"^ao\d+$", examples=["ao3"], description="The AO line driving the masking LED.")
    photodiode_channel: str = Field(pattern=r"^ai\d+$", examples=["ai0"], description="The AI line reading the photodiode, for the laser curve.")
    trigger_channel: str = Field(pattern=r"^PFI\d+$", examples=["PFI0"], description="The PFI line the Behavior board's ZapitTrigger line is cabled to; each trial starts on it.")
    sample_rate: float = Field(gt=0, examples=[100000], description="AO samples a second, per channel; every bank is built at it.")
    laser_min_max_mw: List[float] = Field(min_length=2, max_length=2, description="The laser's lowest and highest power, in mW, at laserMinMaxControlVolts.")
    laser_min_max_control_volts: List[float] = Field(min_length=2, max_length=2, description="The laser's control volts at its lowest and highest power.")
    ceiling_mw: float = Field(gt=0, description="This rig's laser ceiling, in mW: lib_zapit caps every volts value it hands out (bank, aiming, scanner calibration) at it.")
    max_trial_seconds: float = Field(gt=0, description="The longest trial buffer, in seconds: a task's laserCapSeconds above it is refused at Go, and a trial until the outcome is finite at it.")
    early_stop_lead_seconds: float = Field(gt=0, description="How far ahead of the samples generated an early stop writes its ramp, in seconds.")
    microns_per_pixel: float = Field(gt=0, description="The Zapit camera's scale at the sample, in microns a pixel.")
    volts_per_pixel: float = Field(gt=0, description="The galvos' volts a camera pixel, the scanner calibration's starting guess.")
    invert_x_scanner: bool = Field(default=False, description="Whether the X galvo moves against the camera's X.")
    invert_y_scanner: bool = Field(default=False, description="Whether the Y galvo moves against the camera's Y.")
    safety_gate: SafetyGate = Field(description="The laser safety layers this rig was scope-checked with.")
    camera: ZapitCamera = Field(description="Zapit's Basler camera, Camera-Zapit in the data and live view.")

    @model_validator(mode="after")
    def _rising_pairs(self) -> "Zapit":
        for name, pair in (("laserMinMaxMw", self.laser_min_max_mw), ("laserMinMaxControlVolts", self.laser_min_max_control_volts)):
            if not pair[1] > pair[0]:
                raise ValueError(f"zapit.{name} must rise from the first value to the second, not {pair}")
        return self
