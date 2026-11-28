"""The native fields of a rig's rig.yml ``zapit`` block, in one dataclass.

Hand-written per rig; wiring and limits only (calibrations, the sample and the bank live elsewhere). A missing
required field, an unknown key or a bad value is refused with the field named: a rig never runs on a guess.

The legacy MATLAB keys ``host``, ``port`` and ``hardwareTriggered`` may sit beside the native ones and are ignored.
``blankingTimesMs`` (blanking time, onset shift, offset shift) falls back to MATLAB's 0.3, -0.12, 0.25 only when
the key is absent; a present value is used as given. The calibration ``.pfs`` is ``camera.calibrationPfsPath``.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path

import yaml

from open_rigs.zapit.laser import LaserCalibration

_REQUIRED = ("device", "galvoXChannel", "galvoYChannel", "laserChannel", "ledChannel", "photodiodeChannel",
             "triggerChannel", "sampleRate", "laserMinMaxMw", "laserMinMaxControlVolts", "ceilingMw",
             "maxTrialSeconds", "micronsPerPixel", "voltsPerPixel", "camera")
_OPTIONAL = ("invertXScanner", "invertYScanner", "blankingTimesMs")
_LEGACY = ("host", "port", "hardwareTriggered")
MATLAB_BLANKING_MS = (0.3, -0.12, 0.25)
_CAMERA = ("serialNumber", "sessionPfsPath", "calibrationPfsPath")
_CAMERA_FLAGS = ("recordCrop",)


class ZapitConfigError(ValueError):
    pass


@dataclass(frozen=True)
class ZapitCamera:
    serial_number: str
    session_pfs_path: str = "calibration/Zapit-Session.pfs"          # relative to rig.yml's folder
    calibration_pfs_path: str = "calibration/Zapit-Calibration.pfs"
    record_crop: bool = True        # the crop cuts the recording too, so it is fixed from Go; False: the live view only


@dataclass(frozen=True)
class ZapitRig:
    device: str                     # NI device, e.g. "Dev1"
    galvo_x_channel: str            # AO lines, e.g. "ao0"
    galvo_y_channel: str
    laser_channel: str
    led_channel: str
    photodiode_channel: str         # AI line, e.g. "ai0"
    trigger_channel: str            # PFI line the Harp trigger arrives on, e.g. "PFI0"
    sample_rate: float              # Hz
    laser_min_max_mw: tuple[float, float]
    laser_min_max_control_volts: tuple[float, float]
    ceiling_mw: float               # caps every volts value handed out (not the laser-curve staircase); 0 = laser disabled
    max_trial_seconds: float        # ZapitDaq's longest trial buffer in seconds, chosen per rig
    microns_per_pixel: float
    volts_per_pixel: float
    camera: ZapitCamera
    invert_x_scanner: bool = False
    invert_y_scanner: bool = False
    blanking_times_ms: tuple[float, float, float] = MATLAB_BLANKING_MS   # blanking time, onset shift, offset shift

    @classmethod
    def from_dict(cls, block: object) -> "ZapitRig":
        if not isinstance(block, dict):
            raise ZapitConfigError("The rig.yml zapit block must be a mapping")
        missing = [k for k in _REQUIRED if block.get(k) is None]
        if missing:
            raise ZapitConfigError(f"rig.yml zapit block is missing: {', '.join(missing)}")
        unknown = sorted(str(k) for k in set(block) - set(_REQUIRED) - set(_OPTIONAL) - set(_LEGACY))
        if unknown:
            raise ZapitConfigError(f"rig.yml zapit block has unknown keys: {', '.join(unknown)}")
        b = block

        def text(k):
            if not isinstance(b[k], str) or not b[k].strip():
                raise ZapitConfigError(f"zapit.{k} must be a non-empty string")
            return b[k].strip()

        def num(k, positive=True):
            v = b[k]
            if (isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or v < 0
                    or (positive and v == 0)):
                raise ZapitConfigError(f"zapit.{k} must be {'a positive' if positive else 'a non-negative'} number, not {v!r}")
            return float(v)

        def pair(k):
            v = b[k]
            if (not isinstance(v, (list, tuple)) or len(v) != 2 or any(isinstance(x, bool) or not isinstance(
                    x, (int, float)) or not math.isfinite(x) for x in v) or not v[1] > v[0]):
                raise ZapitConfigError(f"zapit.{k} must be two numbers rising from the first to the second")
            return float(v[0]), float(v[1])

        def flag(k):
            v = b.get(k, False)
            if not isinstance(v, bool):
                raise ZapitConfigError(f"zapit.{k} must be true or false")
            return v

        blanking = MATLAB_BLANKING_MS
        if "blankingTimesMs" in b:
            v = b["blankingTimesMs"]
            if (not isinstance(v, (list, tuple)) or len(v) != 3 or any(
                    isinstance(x, bool) or not isinstance(x, (int, float)) or not math.isfinite(x) for x in v) or v[0] <= 0):
                raise ZapitConfigError("zapit.blankingTimesMs must be three finite numbers, the first positive")
            blanking = (float(v[0]), float(v[1]), float(v[2]))
        c = b["camera"]
        if not isinstance(c, dict) or set(c) - set(_CAMERA + _CAMERA_FLAGS) or any(
                not isinstance(c.get(k, "x"), str) or not c.get(k, "x").strip() for k in _CAMERA):
            raise ZapitConfigError(f"zapit.camera must be a mapping of {', '.join(_CAMERA)} (non-empty text) and recordCrop (true or false)")
        if not isinstance(c.get("recordCrop", True), bool):
            raise ZapitConfigError(f"zapit.camera.recordCrop must be true or false, not {c['recordCrop']!r}")
        if "serialNumber" not in c:
            raise ZapitConfigError("zapit.camera.serialNumber is missing")
        camera = ZapitCamera(c["serialNumber"].strip(), *(c[k].strip() for k in _CAMERA[1:] if k in c),
                             record_crop=c.get("recordCrop", True))
        mw, cv = pair("laserMinMaxMw"), pair("laserMinMaxControlVolts")
        rig = cls(text("device"), text("galvoXChannel"), text("galvoYChannel"), text("laserChannel"),
                  text("ledChannel"), text("photodiodeChannel"), text("triggerChannel"), num("sampleRate"), mw, cv,
                  num("ceilingMw", positive=False), num("maxTrialSeconds"),
                  num("micronsPerPixel"), num("voltsPerPixel"), camera, flag("invertXScanner"),
                  flag("invertYScanner"), blanking)
        return rig

    def laser(self, fit=None) -> LaserCalibration:
        return LaserCalibration(self.laser_min_max_control_volts, self.laser_min_max_mw, fit)


def read_rig_zapit(rig_yml, require: bool = True) -> ZapitRig | None:
    """The ``zapit`` block of a rig.yml (path, or the loaded mapping). No block: None when ``require`` is False
    (a desk or a rig without Zapit), otherwise refused."""
    if isinstance(rig_yml, (str, Path)):
        rig_yml = yaml.safe_load(Path(rig_yml).read_text(encoding="utf-8")) or {}
    if not isinstance(rig_yml, dict):
        raise ZapitConfigError("rig.yml must be a mapping with a zapit block")
    block = rig_yml.get("zapit")
    if block is None:
        if require:
            raise ZapitConfigError("rig.yml has no zapit block")
        return None
    return ZapitRig.from_dict(block)
