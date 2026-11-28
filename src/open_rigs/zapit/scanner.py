"""Per-session scanner calibration from saved frames, and Measure Accuracy.

The workflow steps through ``calibration_plan`` (beam volts per point, laser at the calibration power, 3 frames
skipped, 1 lossless frame saved per point, 4 laser-off backgrounds); ``fit_scanner`` finds the beam in each frame and
fits the transform exactly as the app's ``Zapit.calibrate_scanners`` does: a spot no brighter than 1.5 times the
laser-off flicker, or more than 3 mm from its aim, is a glint and skipped; points far off the fit are dropped; the
model (similarity, affine, polynomial) is chosen by leave-one-out pointing error; a poor fit is refused.
``accuracy_plan`` and ``measure_accuracy`` repeat the steps between the grid points through the fit.

Frame folders: ``background_<k>.png`` (laser off) and ``point_<index>.png``, see ``load_frames``.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field, replace
from pathlib import Path

import numpy as np

from open_rigs.zapit import calibration as calib
from open_rigs.zapit.laser import LaserCalibration
from open_rigs.zapit.safety import laser_volts
from open_rigs.zapit.transforms import (Affine, FitInfo, Polynomial, ScannerMapping, select_scanner_transform)

MAX_CALIBRATION_RESIDUAL_UM = 100.0   # a fit with a worse mean residual is refused
N_BACKGROUND_FRAMES = 4               # laser-off frames; the background is their per-pixel maximum
FLICKER_FACTOR = 1.5                  # a beam must be this many times brighter than the background's flicker
SPOT_SMOOTH_PX = 2.0                  # Gaussian smoothing of the difference image before thresholding
MAX_AIM_ERROR_MM = 3.0                # a spot further than this from its aim is a glint, not the beam
SPARSE_CALIBRATION = 0.5              # a fit from under this fraction of the grid is accepted with a warning
MIN_POINTS = 4
SKIP_FRAMES = 3                       # frames the workflow skips after each move before saving one
MISS_FRAME = "no frame saved"


@dataclass(frozen=True)
class ScannerSettings:
    """What the per-session fit needs about the camera image and the grid (rig settings, not the fit)."""
    microns_per_pixel: float
    volts_per_pixel: float
    image_size: tuple[int, int]               # (width, height) of the saved frames, pixels
    invert_x: bool = False
    invert_y: bool = False
    roi_offset_mm: tuple[float, float] = (0.0, 0.0)   # ROI centre from the sensor centre (the scanner origin)
    calibration_power_mw: float = 2.0         # a 10 mW beam glowed into a disc over the threshold on bone (B2, 24 Nov 2026)
    point_spacing_mm: float = 2.0
    buffer_mm: float = 1.5                    # 2 mm left 7 points on B2's skull, 1.5 mm 12 (24 Nov 2026)
    area_threshold: int = 500
    # Where on the still the grid and the accuracy points may go (a boolean mask the still's size, before the buffer);
    # None for the whole still. Set by with_found_area or with_rectangle_area.
    area: np.ndarray | None = field(default=None, compare=False, repr=False)

    @classmethod
    def from_rig(cls, rig, image_size: tuple[int, int], roi_offset_mm=(0.0, 0.0), **grid) -> "ScannerSettings":
        """The wiring from the rig.yml zapit block (``ZapitRig``); ``grid``: calibration_power_mw, point_spacing_mm,
        buffer_mm, area_threshold."""
        return cls(rig.microns_per_pixel, rig.volts_per_pixel, (image_size[0], image_size[1]), rig.invert_x_scanner,
                   rig.invert_y_scanner, tuple(roi_offset_mm), **grid)

    def nominal_mapping(self, transform=None) -> ScannerMapping:
        return ScannerMapping(self.microns_per_pixel, self.volts_per_pixel, self.invert_x, self.invert_y, transform)

    def pixels_to_mm(self, xy_px) -> np.ndarray:
        return self.nominal_mapping().pixels_to_mm(xy_px, self.image_size) + np.asarray(self.roi_offset_mm)

    def extent_mm(self) -> tuple[float, float, float, float]:
        w, h = self.image_size
        x0, y0 = self.pixels_to_mm([0, 0])[0]
        mm = self.microns_per_pixel * 1e-3
        return float(x0), float(y0), w * mm, h * mm


@dataclass
class ScanPlan:
    """Beam positions the workflow steps through: aim (sample mm), the scanner volts for each, and the laser's
    control volts for the calibration power, capped by the rig's ceiling."""
    points_mm: np.ndarray        # (N, 2)
    volts: np.ndarray            # (N, 2) galvo X, Y
    laser_volts: float

    def __len__(self) -> int:
        return len(self.points_mm)


def _area_grid(settings: ScannerSettings) -> tuple[np.ndarray, np.ndarray]:
    """The grid inside settings.area, in pixels, and the region it had to fall in (the area less the buffer)."""
    assert settings.area is not None
    px_per_mm = 1e3 / settings.microns_per_pixel
    return calib.area_grid_px(settings.area, settings.point_spacing_mm * px_per_mm, settings.buffer_mm * px_per_mm)


def grid_points_mm(settings: ScannerSettings) -> np.ndarray:
    """The calibration grid in sample mm: over the whole still less the buffer, or only inside settings.area less the
    buffer. Pixels become sample mm through ``pixels_to_mm``, so an area off the image centre, and an ROI off the
    sensor centre (roi_offset_mm), are aimed where they are."""
    if settings.area is None:
        return calib.calibration_grid(settings.image_size, settings.microns_per_pixel * 1e-3,
                                      settings.point_spacing_mm, settings.buffer_mm, settings.roi_offset_mm)
    grid, _ = _area_grid(settings)
    return settings.pixels_to_mm(grid) if len(grid) else np.zeros((0, 2))


def with_area(settings: ScannerSettings, mask: np.ndarray) -> tuple[ScannerSettings | None, str]:
    """``settings`` with the grid kept inside ``mask``, or (None, why) when it leaves under MIN_POINTS points."""
    w, h = settings.image_size
    if np.shape(mask) != (h, w):
        return None, f"the area is {np.shape(mask)[1]} x {np.shape(mask)[0]} px, not the still's {w} x {h}"
    out = replace(settings, area=np.asarray(mask, bool))
    n = len(grid_points_mm(out))
    if n < MIN_POINTS:
        return None, f"it holds {n} grid points less the {settings.buffer_mm:g} mm buffer, and at least {MIN_POINTS} are needed"
    return out, f"{n} grid points"


def with_found_area(settings: ScannerSettings, still: np.ndarray) -> tuple[ScannerSettings | None, str]:
    """``settings`` with the grid on the target found in ``still`` (calibration.find_target): the skull inside the
    headplate, or a card. (None, why) when no target is found, it is ambiguous, or it is too small for the grid."""
    mask, how = calib.find_target(still)
    if mask is None:
        return None, how
    out, why = with_area(settings, mask)
    return (out, f"{how}: {why}") if out is not None else (None, f"{how}: {why}")


def with_rectangle_area(settings: ScannerSettings, rect) -> tuple[ScannerSettings | None, str]:
    """``settings`` with the grid inside a rectangle a person drew (u0, v0, u1, v1: fractions of the still)."""
    out, why = with_area(settings, calib.rectangle_mask(settings.image_size, rect))
    return (out, f"the rectangle drawn: {why}") if out is not None else (None, f"the rectangle drawn: {why}")


def calibration_plan(settings: ScannerSettings, laser: LaserCalibration, ceiling_mw: float) -> ScanPlan:
    """The grid for the scanner calibration, aimed through the nominal mapping (no fit yet)."""
    mm = grid_points_mm(settings)
    if len(mm) < MIN_POINTS:
        raise ValueError(f"The grid has only {len(mm)} points and at least {MIN_POINTS} are needed: lower the point "
                         f"spacing or the border buffer, or enlarge the ROI or the area")
    return ScanPlan(mm, settings.nominal_mapping().mm_to_volts(mm),
                    laser_volts(laser, settings.calibration_power_mw, ceiling_mw))


def accuracy_points_mm(settings: ScannerSettings) -> np.ndarray:
    """Points half a grid step off the calibration grid, inside it (and inside the area less its buffer, when the
    grid has one): the fit never saw them. Never the grid's own points: too few is said by accuracy_plan."""
    if settings.area is not None:
        grid, region = _area_grid(settings)
        half = settings.point_spacing_mm * 1e3 / settings.microns_per_pixel / 2
        kept = np.zeros((0, 2))
        # Diagonally between, then also half a step along each axis while too few fit: a small area keeps enough.
        for step in ((half, half), (half, 0.0), (0.0, half)):
            between = grid + np.array(step)
            kept = np.vstack([kept, between[calib.inside(region, between)]])
            if len(kept) >= MIN_POINTS:
                break
        return settings.pixels_to_mm(kept) if len(kept) else np.zeros((0, 2))
    grid = calib.calibration_grid(settings.image_size, settings.microns_per_pixel * 1e-3, settings.point_spacing_mm,
                                  settings.buffer_mm, settings.roi_offset_mm)
    if len(grid) < 2:
        return np.zeros((0, 2))
    lo, hi = grid.min(axis=0), grid.max(axis=0)
    between = grid + settings.point_spacing_mm / 2
    inside = np.all((between >= lo - 1e-9) & (between <= hi + 1e-9), axis=1)
    return between[inside]


def accuracy_plan(settings: ScannerSettings, fit: "ScannerFit", laser: LaserCalibration, ceiling_mw: float) -> ScanPlan:
    """The between-grid points aimed through the session's fit."""
    if not fit.ok or fit.mapping is None:
        raise ValueError("Measure Accuracy needs an accepted scanner fit")
    mm = accuracy_points_mm(settings)
    if len(mm) < 2:
        raise ValueError(f"Measure Accuracy needs at least 2 points the fit never saw, and this grid has {len(mm)}: "
                         "enlarge the area or lower the point spacing")
    return ScanPlan(mm, fit.mapping.mm_to_volts(mm), laser_volts(laser, settings.calibration_power_mw, ceiling_mw))


# ------------------------------------------------------------------------------------------------ frames
_POINT_RE = re.compile(r"^point_(\d+)\.png$")
_BACKGROUND_RE = re.compile(r"^background_(\d+)\.png$")


def load_frames(folder) -> tuple[dict[int, np.ndarray], list[np.ndarray]]:
    """Frames of one run: ``point_<index>.png`` (index into the plan, any zero padding) and ``background_<k>.png``
    (laser off). Lossless grey PNGs, read as saved (8 or 16 bit)."""
    import cv2
    folder = Path(folder)
    points, backgrounds = {}, []
    for p in sorted(folder.glob("*.png")):
        m, b = _POINT_RE.match(p.name), _BACKGROUND_RE.match(p.name)
        if not (m or b):
            continue
        img = cv2.imread(str(p), cv2.IMREAD_UNCHANGED)
        if img is None:
            raise ValueError(f"{p} is not a readable PNG")
        if img.ndim == 3:
            img = img[..., 0]
        if m:
            points[int(m.group(1))] = img
        elif b:
            backgrounds.append((int(b.group(1)), img))
    return points, [img for _, img in sorted(backgrounds, key=lambda t: t[0])]


def save_frames(folder, points: dict[int, np.ndarray], backgrounds: list[np.ndarray]) -> None:
    """The writer of the layout ``load_frames`` reads (tests, and the simulated workflow)."""
    import cv2
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    for i, img in points.items():
        cv2.imwrite(str(folder / f"point_{i:04d}.png"), img)
    for k, img in enumerate(backgrounds):
        cv2.imwrite(str(folder / f"background_{k}.png"), img)


# ---------------------------------------------------------------------------------------------- detection
def _background(backgrounds):
    if not backgrounds:
        return None, 0.0
    off = [np.asarray(b, dtype=np.float64) for b in backgrounds]
    floor = FLICKER_FACTOR * calib.flicker_floor(off, SPOT_SMOOTH_PX) if len(off) > 1 else 0.0
    return np.max(off, axis=0), floor


def _detect(frames: dict[int, np.ndarray], targets_mm: np.ndarray, backgrounds, s: ScannerSettings):
    """Spots found (list of SpotMeasurement) and the miss reasons counted, with where each miss was aimed."""
    background, min_peak = _background(backgrounds)
    crop = int(round(375 / s.microns_per_pixel))
    found, misses, missed_at = [], {}, []
    for i, target in enumerate(targets_mm):
        frame = frames.get(i)
        why = MISS_FRAME
        if frame is not None:
            hit, why = calib.detect_beam(np.asarray(frame, dtype=np.float64), background, s.area_threshold,
                                         crop_half_px=crop, min_peak=min_peak, smooth_sigma=SPOT_SMOOTH_PX)
            if hit is not None:
                centroid, area, crop_img = hit
                actual = s.pixels_to_mm(centroid)[0]
                if np.hypot(*(actual - target)) <= MAX_AIM_ERROR_MM:
                    found.append(calib.SpotMeasurement(np.asarray(target, float), actual, centroid, area, crop_img))
                    continue
                why = calib.MISS_FAR
        misses[why] = misses.get(why, 0) + 1
        missed_at.append(np.asarray(target, float))
    return found, misses, missed_at


def _misses_text(misses: dict[str, int]) -> str:
    return "; ".join(f"{n} missed: {why}" for why, n in sorted(misses.items(), key=lambda kv: -kv[1]))


# --------------------------------------------------------------------------------------------------- fit
@dataclass
class ScannerFit:
    ok: bool
    report: str                          # what was found and, when refused, why
    mapping: ScannerMapping | None       # nominal mapping with the fitted transform; None when refused
    transform: Affine | Polynomial | None = None
    info: FitInfo | None = None
    n_found: int = 0
    n_points: int = 0
    mean_residual_um: float = float("nan")
    misses: dict = field(default_factory=dict)
    map: calib.CalibrationMap | None = None
    warnings: list = field(default_factory=list)

    def to_dict(self) -> dict:
        """The record saved with the session: JSON-safe, with the mapping the bank was aimed through and its
        bank.scanner_fit_hash, so ``mapping_from_record`` rebuilds it from this record alone."""
        from open_rigs.zapit.bank import scanner_fit_hash
        t, m = self.transform, self.mapping
        fit = None if t is None else ({"order": t.order, "forward": t.forward_coef.tolist(),
                                       "inverse": t.inverse_coef.tolist(), "linear": t.linear.matrix.tolist()}
                                      if isinstance(t, Polynomial) else t.to_dict())
        return {"ok": self.ok, "report": self.report, "n_found": self.n_found, "n_points": self.n_points,
                "mean_residual_um": _finite(self.mean_residual_um), "misses": self.misses, "warnings": self.warnings,
                "chosen": None if self.info is None else self.info.chosen,
                # A model too few points could try has an infinite error: null, as strict JSON has no Infinity.
                "loo_rms_um": None if self.info is None else {k: _finite(v) for k, v in self.info.loo_rms_um.items()},
                "fit": fit,
                "mapping": None if m is None else {"microns_per_pixel": m.microns_per_pixel,
                                                   "volts_per_pixel": m.volts_per_pixel, "invert_x": m.invert_x,
                                                   "invert_y": m.invert_y},
                "scanner_fit_hash": None if m is None else scanner_fit_hash(m)}


def mapping_from_record(record: dict) -> ScannerMapping:
    """The mapping a ScannerFit record (``ScannerFit.to_dict``, as filed with a session) was saved with."""
    fit, m = record["fit"], record["mapping"]
    if fit is None:
        transform = None
    elif "order" in fit:
        transform = Polynomial(int(fit["order"]), np.asarray(fit["forward"], float), np.asarray(fit["inverse"], float),
                               Affine.from_dict({"matrix": fit["linear"]}))
    else:
        transform = Affine.from_dict(fit)
    return ScannerMapping(m["microns_per_pixel"], m["volts_per_pixel"], m["invert_x"], m["invert_y"], transform)


def _finite(x: float) -> float | None:
    """`x` for a record, or None where it is NaN or infinite (none measured): strict JSON has neither."""
    return float(x) if np.isfinite(x) else None


def _refused(report, n_found, n_points, misses, m=None) -> ScannerFit:
    return ScannerFit(False, report, None, n_found=n_found, n_points=n_points, misses=misses, map=m)


def fit_scanner(plan: ScanPlan, frames: dict[int, np.ndarray], backgrounds: list[np.ndarray],
                settings: ScannerSettings) -> ScannerFit:
    """Detect the beam in ``frames`` (index into ``plan``), reject glints, and fit. Never raises for a bad
    run: the returned fit has ``ok`` False and ``report`` says why (too few points, a poor fit)."""
    found, misses, missed_at = _detect(frames, plan.points_mm, backgrounds, settings)
    n_points = len(plan)
    text = f"{len(found)} of {n_points} points found"
    if misses:
        text += f" ({_misses_text(misses)})"
    seen = np.array([m.actual_mm for m in found]).reshape(-1, 2)
    cmap = calib.CalibrationMap(seen, np.full(len(seen), np.nan), np.array(missed_at).reshape(-1, 2))
    if len(found) < MIN_POINTS:
        return _refused(f"Only {text}; at least {MIN_POINTS} are needed", len(found), n_points, misses, cmap)
    target = np.array([m.target_mm for m in found])
    x0, y0, w, h = settings.extent_mm()
    tf, info = select_scanner_transform(target, seen, (x0, x0 + w, y0, y0 + h))
    cmap.residual_um = np.linalg.norm(tf.inverse(seen) - target, axis=1) * 1e3
    mean_um = float(info.residuals_um.mean())
    if info.n_outliers:
        text += f"; {info.n_outliers} disagreeing with the rest ignored"
    rms = info.loo_rms_um.get(info.chosen, np.inf)
    text += f"; {info.chosen} fit" + (f" (about {rms:.0f} um between points)" if np.isfinite(rms) else "")
    if mean_um > MAX_CALIBRATION_RESIDUAL_UM:
        out = _refused(f"{text}, but the fit is poor: mean residual {mean_um:.0f} um, above "
                       f"{MAX_CALIBRATION_RESIDUAL_UM:.0f} um (glints taken for the beam, or a mirrored image "
                       f"that needs an invert flag)", len(found), n_points, misses, cmap)
        out.mean_residual_um, out.info = mean_um, info
        return out
    warnings = []
    if len(found) < SPARSE_CALIBRATION * n_points:
        warnings.append("under half the grid was found, so pointing is reliable only near those points")
    return ScannerFit(True, text, settings.nominal_mapping(tf), tf, info, len(found), n_points, mean_um, misses,
                      cmap, warnings)


def fit_scanner_run(plan: ScanPlan, folder, settings: ScannerSettings) -> ScannerFit:
    """``fit_scanner`` on the frames saved in ``folder``."""
    points, backgrounds = load_frames(folder)
    return fit_scanner(plan, points, backgrounds, settings)


# ------------------------------------------------------------------------------------- Measure Accuracy
@dataclass
class Accuracy:
    n_found: int
    n_points: int
    errors_um: np.ndarray
    misses: dict
    map: calib.CalibrationMap

    @property
    def mean_um(self) -> float:
        return float(self.errors_um.mean()) if len(self.errors_um) else float("nan")

    @property
    def max_um(self) -> float:
        return float(self.errors_um.max()) if len(self.errors_um) else float("nan")

    def to_dict(self) -> dict:
        return {"n_found": self.n_found, "n_points": self.n_points, "mean_um": _finite(self.mean_um),
                "max_um": _finite(self.max_um), "errors_um": self.errors_um.tolist(), "misses": self.misses}


def measure_accuracy(plan: ScanPlan, frames: dict[int, np.ndarray], backgrounds: list[np.ndarray],
                     settings: ScannerSettings) -> Accuracy:
    """Where the calibrated beam landed against where it was aimed, at the between-grid points of ``plan``."""
    found, misses, missed_at = _detect(frames, plan.points_mm, backgrounds, settings)
    err = np.array([m.error_um for m in found], dtype=float)
    cmap = calib.CalibrationMap(np.array([m.actual_mm for m in found]).reshape(-1, 2), err,
                                np.array(missed_at).reshape(-1, 2))
    return Accuracy(len(found), len(plan), err, misses, cmap)


def measure_accuracy_run(plan: ScanPlan, folder, settings: ScannerSettings) -> Accuracy:
    points, backgrounds = load_frames(folder)
    return measure_accuracy(plan, points, backgrounds, settings)
