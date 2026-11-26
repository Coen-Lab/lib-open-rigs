"""Scanner calibration helpers: the grid of target points and beam spot detection."""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy import ndimage


def calibration_grid(image_size: tuple[int, int], mm_per_pixel: float, spacing_mm: float, buffer_mm: float,
                     roi_offset_mm: tuple[float, float] = (0.0, 0.0)) -> np.ndarray:
    """(N, 2) target beam positions in sample mm, centred on the image, ``buffer_mm`` from the edges.

    ``roi_offset_mm`` is the displacement of the current ROI centre from the full
    sensor centre (the scanner origin), so a cropped view still targets the beam
    at the right place.
    """
    half = np.asarray(image_size, dtype=float) * mm_per_pixel / 2
    axes = []
    for h in half:
        pts = np.arange(-h + buffer_mm, h - buffer_mm + 1e-9, spacing_mm)
        if len(pts):
            pts = pts + ((h - buffer_mm) - pts[-1]) / 2   # centre the grid
        axes.append(pts)
    cols, rows = np.meshgrid(axes[0], axes[1], indexing="ij")
    grid = np.c_[cols.ravel(), rows.ravel()]
    return grid + np.asarray(roi_offset_mm, dtype=float)


TARGET_SIDE_PX = 256        # the still is shrunk to about this many pixels across to find the target
TARGET_MIN_FRACTION = 0.02  # a target smaller than this fraction of the still is not one
TARGET_RIVAL = 0.5          # a second bright region at least this fraction of the largest makes the target ambiguous
TARGET_FLAT = 0.15          # a still whose bright and dark parts differ by less than this (of the bright) is all target


def find_target(still: np.ndarray) -> tuple[np.ndarray | None, str]:
    """Where the beam can be aimed on a still: (a boolean mask the still's size, how it was found), or (None, why not).

    The target is the bright region, as one piece: the still is smoothed and split at Otsu's threshold, and bright
    parts narrower than a sixteenth of the still (a headplate ring's edge, glints) are opened away, so on a mouse it
    is the skull inside the ring. A still with no clear bright part (a card filling the view) is all target. Refused:
    no bright region, one under ``TARGET_MIN_FRACTION`` of the still, or a second one at least ``TARGET_RIVAL`` of it."""
    import cv2
    img = np.asarray(still, dtype=np.float32)
    if img.ndim == 3:
        img = img[..., 0]
    h, w = img.shape
    f = max(1, int(round(max(h, w) / TARGET_SIDE_PX)))
    small = ndimage.gaussian_filter(cv2.resize(img, (max(1, w // f), max(1, h // f)), interpolation=cv2.INTER_AREA), 3)
    lo, hi = float(small.min()), float(small.max())
    if hi <= 0:
        return None, "the still is dark"
    u8 = np.clip((small - lo) / max(hi - lo, 1e-6) * 255, 0, 255).astype(np.uint8)
    t, _ = cv2.threshold(u8, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    bright = u8 > t
    if bright.all() or not bright.any() or (
            np.mean(small[bright]) - np.mean(small[~bright]) < TARGET_FLAT * np.mean(small[bright])):
        return np.ones((h, w), bool), "the whole still (no bright part stands out)"
    r = max(1, min(small.shape) // 16)
    disc = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1))
    opened = ndimage.binary_fill_holes(cv2.morphologyEx(bright.astype(np.uint8), cv2.MORPH_OPEN, disc) > 0)
    labels, n = ndimage.label(opened)
    if n == 0:
        return None, "no bright region wide enough"
    sizes = np.asarray(ndimage.sum(opened, labels, range(1, n + 1)), float)
    order = np.argsort(sizes)[::-1]
    if sizes[order[0]] < TARGET_MIN_FRACTION * opened.size:
        return None, f"the brightest region is under {TARGET_MIN_FRACTION:.0%} of the still"
    if n > 1 and sizes[order[1]] >= TARGET_RIVAL * sizes[order[0]]:
        return None, "two bright regions of about the same size"
    mask = cv2.resize((labels == order[0] + 1).astype(np.uint8), (w, h), interpolation=cv2.INTER_NEAREST) > 0
    return mask, f"the bright region ({mask.mean():.0%} of the still)"


def rectangle_mask(image_size: tuple[int, int], rect: tuple[float, float, float, float]) -> np.ndarray:
    """A boolean mask the still's size, True inside ``rect`` (u0, v0, u1, v1: fractions of the width and height,
    either corner first)."""
    w, h = image_size
    u0, u1 = sorted((float(rect[0]), float(rect[2])))
    v0, v1 = sorted((float(rect[1]), float(rect[3])))
    mask = np.zeros((h, w), bool)
    mask[int(round(max(v0, 0) * h)):int(round(min(v1, 1) * h)), int(round(max(u0, 0) * w)):int(round(min(u1, 1) * w))] = True
    return mask


def area_grid_px(mask: np.ndarray, spacing_px: float, buffer_px: float) -> tuple[np.ndarray, np.ndarray]:
    """Grid points (col, row) inside ``mask`` at least ``buffer_px`` from its edge and from the still's edge, and the
    region they had to fall in (the mask less the buffer). The grid is ``spacing_px`` apart, centred on that region's
    bounding box, as ``calibration_grid`` centres its grid on the image."""
    inner = ndimage.distance_transform_edt(np.pad(np.asarray(mask, bool), 1))[1:-1, 1:-1] >= max(buffer_px, 1.0)
    rows, cols = np.nonzero(inner)
    if not len(rows):
        return np.zeros((0, 2)), inner
    axes = []
    for lo, hi in ((cols.min(), cols.max()), (rows.min(), rows.max())):
        pts = np.arange(lo, hi + 1e-9, spacing_px)
        axes.append(pts + (hi - pts[-1]) / 2)
    cc, rr = np.meshgrid(*axes, indexing="ij")
    grid = np.c_[cc.ravel(), rr.ravel()]
    return grid[inside(inner, grid)], inner


def inside(region: np.ndarray, points_px: np.ndarray) -> np.ndarray:
    """Which (col, row) points fall on a True pixel of ``region``."""
    pts = np.rint(np.asarray(points_px, float)).astype(int).reshape(-1, 2)
    h, w = region.shape
    ok = (pts[:, 0] >= 0) & (pts[:, 0] < w) & (pts[:, 1] >= 0) & (pts[:, 1] < h)
    ok[ok] = region[pts[ok, 1], pts[ok, 0]]
    return ok


@dataclass
class SpotMeasurement:
    target_mm: np.ndarray        # commanded position (sample mm)
    actual_mm: np.ndarray        # detected position (sample mm)
    actual_px: np.ndarray        # detected centroid (col, row) in the current image
    area_px: int
    crop: np.ndarray             # small image around the spot

    @property
    def error_mm(self) -> np.ndarray:
        return self.target_mm - self.actual_mm

    @property
    def error_um(self) -> float:
        return float(np.linalg.norm(self.error_mm) * 1e3)


@dataclass
class CalibrationMap:
    """What one scanner calibration saw, for drawing on the image: where the beam was found (sample mm), how far
    each of those points sits off the fit (um; NaN before a fit), and where the beam was aimed but not found."""
    found_mm: np.ndarray       # (N, 2)
    residual_um: np.ndarray    # (N,)
    missed_mm: np.ndarray      # (M, 2)


# Why a frame gave no spot, as counted and reported by the scanner calibration.
MISS_NONE = "no bright spot"
MISS_DIM = "dimmer than the background flicker"
MISS_SEVERAL = "several bright spots"
MISS_LARGE = "spot larger than the size threshold"
MISS_FAR = "spot too far from where the beam was aimed"
MISS_EDGE = "spot cut off by the image edge"


def detect_beam(frame: np.ndarray, background: np.ndarray | None, area_threshold: int, crop_half_px: int = 20,
                min_peak: float = 0.0, smooth_sigma: float = 0.0
                ) -> tuple[tuple[np.ndarray, int, np.ndarray] | None, str | None]:
    """``find_beam_spot`` that also says why it found nothing: ``(found, None)`` or ``(None, MISS_...)``.

    ``smooth_sigma`` (pixels) smooths the background-subtracted frame first, so single noisy pixels cannot set
    the peak; ``min_peak`` refuses a spot no brighter than that above the background (the flicker floor).
    """
    # float32: grey levels and their 2 px smoothing need no more, and the smoothing is a third quicker.
    f = np.asarray(frame, dtype=np.float32)
    if background is not None:
        f = f - np.asarray(background, dtype=np.float32)
        frac = 0.9
    else:
        frac = 0.5
    if smooth_sigma > 0:
        f = ndimage.gaussian_filter(f, smooth_sigma)
    peak = f.max()
    if peak <= 0:
        return None, MISS_NONE
    if peak <= min_peak:
        return None, MISS_DIM
    bw = f > peak * frac
    _, n = ndimage.label(bw)
    if n != 1:
        return None, MISS_SEVERAL
    area = int(bw.sum())
    if area > area_threshold:
        return None, MISS_LARGE
    # A spot at the image edge is partly cut off, which pulls its centroid inward (by ~0.1 mm on the rig's
    # geometry): its tail must fit in the frame, a spot radius plus the smoothing on every side.
    rows, cols = np.nonzero(bw)
    margin = np.sqrt(area / np.pi) + 2 * smooth_sigma + 1
    h, w = bw.shape
    if rows.min() < margin or cols.min() < margin or rows.max() > h - 1 - margin or cols.max() > w - 1 - margin:
        return None, MISS_EDGE
    row, col = rows.mean(), cols.mean()   # bw's centre of mass, from the pixels already found
    c, r = int(round(col)), int(round(row))
    crop = np.asarray(frame)[max(0, r - crop_half_px):r + crop_half_px + 1,
                             max(0, c - crop_half_px):c + crop_half_px + 1]
    return (np.array([col, row]), area, crop), None


def find_beam_spot(frame: np.ndarray, background: np.ndarray | None, area_threshold: int,
                   crop_half_px: int = 20) -> tuple[np.ndarray, int, np.ndarray] | None:
    """Locate a single bright spot. Returns (centroid (col,row), area, crop) or None.

    Thresholds the background-subtracted frame at 90 % of its maximum (50 % when
    no background is given) and accepts exactly one region no larger than
    ``area_threshold`` pixels.
    """
    return detect_beam(frame, background, area_threshold, crop_half_px)[0]


def flicker_floor(frames: list[np.ndarray], smooth_sigma: float = 2.0, percentile: float = 99.9) -> float:
    """How much the smoothed laser-off ``frames`` change from frame to frame: a beam must rise clearly above it.

    The ``percentile`` of the per-pixel range, not its maximum (MATLAB Zapit's choice): one small glint that
    flickers fully would otherwise set a floor no beam reaches, while the per-pixel maximum background already
    cancels that glint where it is."""
    stack = np.stack([ndimage.gaussian_filter(np.asarray(f, dtype=np.float32), smooth_sigma) for f in frames])
    return float(np.percentile(stack.max(axis=0) - stack.min(axis=0), percentile))
