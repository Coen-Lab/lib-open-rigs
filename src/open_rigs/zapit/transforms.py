"""Coordinate transforms between stereotaxic, sample (camera mm), pixel and scanner volts.

Three frames matter:

* **stereotaxic**: mm relative to bregma, [ML, AP].
* **sample**: mm in the camera image, origin at the image centre, x along columns
  and y along rows. The sample calibration (two clicked reference points) maps
  stereotaxic to sample.
* **volts**: scanner command voltages. The scanner calibration is a transform fitted
  between where the beam was commanded (sample mm, assuming the nominal
  volts-per-pixel scaling) and where the camera saw it: a similarity, an affine (two
  galvos of unequal gain) or a polynomial (optical distortion), chosen by
  ``select_scanner_transform`` on leave-one-out pointing error.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np


# --------------------------------------------------------------------------- stereotaxic -> sample
def rotate_and_scale(points: np.ndarray, ref_stereotaxic: np.ndarray, ref_sample: np.ndarray) -> np.ndarray:
    """Map stereotaxic points onto the sample using two reference points.

    points:          (N, 2) [ML, AP] stereotaxic mm
    ref_stereotaxic: (2, 2) rows are bregma and a second point, [ML, AP]
    ref_sample:      (2, 2) the same two points as observed in the camera, [x, y] mm

    Returns (N, 2) sample coordinates. Rotation, uniform scale and translation only.
    """
    points = np.atleast_2d(np.asarray(points, dtype=float))
    ref_stereotaxic = np.asarray(ref_stereotaxic, dtype=float)
    ref_sample = np.asarray(ref_sample, dtype=float)

    translation = ref_sample[0]
    zeroed = ref_sample - ref_sample[0]
    th_sample = np.arctan2(zeroed[1, 1], zeroed[1, 0])
    th_ref = np.arctan2(ref_stereotaxic[1, 1], ref_stereotaxic[1, 0])
    r_sample = np.hypot(zeroed[1, 0], zeroed[1, 1])
    r_ref = np.hypot(ref_stereotaxic[1, 0], ref_stereotaxic[1, 1])
    if r_ref == 0:
        raise ValueError("Stereotaxic reference points coincide")
    rot = rotation_matrix(th_sample - th_ref)
    scale = r_sample / r_ref
    return (rot @ points.T * scale).T + translation


def rotation_matrix(theta: float) -> np.ndarray:
    c, s = np.cos(theta), np.sin(theta)
    return np.array([[c, -s], [s, c]])


# --------------------------------------------------------------------------- scanner transforms
def _check_pairs(src, dst, minimum: int) -> tuple[np.ndarray, np.ndarray]:
    src = np.asarray(src, dtype=float)
    dst = np.asarray(dst, dtype=float)
    if src.shape != dst.shape or src.ndim != 2 or src.shape[1] != 2:
        raise ValueError("src and dst must both be (N, 2)")
    if len(src) < minimum:
        raise ValueError(f"At least {minimum} point pairs are needed")
    return src, dst


@dataclass(frozen=True)
class Affine:
    """2-D affine transform  y = A x + t  stored as a 3x3 matrix: unequal scales on the two axes and shear, which
    two galvos of unequal gain produce. Fitted by least squares from commanded beam positions (x) to observed
    positions (y)."""
    matrix: np.ndarray  # 3x3 homogeneous
    kind = "affine"

    @classmethod
    def fit(cls, src: np.ndarray, dst: np.ndarray) -> "Affine":
        src, dst = _check_pairs(src, dst, 3)
        h = np.c_[src, np.ones(len(src))]
        coef = np.linalg.lstsq(h, dst, rcond=None)[0]   # (3, 2)
        return cls(np.vstack([coef.T, [0.0, 0.0, 1.0]]))

    @property
    def scale(self) -> float:
        """The mean scale: the square root of the area change."""
        return float(np.sqrt(abs(np.linalg.det(self.matrix[:2, :2]))))

    @property
    def rotation_deg(self) -> float:
        m = self.matrix
        return float(np.degrees(np.arctan2(m[1, 0] - m[0, 1], m[0, 0] + m[1, 1])))

    def forward(self, xy: np.ndarray) -> np.ndarray:
        xy = np.atleast_2d(np.asarray(xy, dtype=float))
        h = np.c_[xy, np.ones(len(xy))]
        return (self.matrix @ h.T).T[:, :2]

    def inverse(self, xy: np.ndarray) -> np.ndarray:
        xy = np.atleast_2d(np.asarray(xy, dtype=float))
        h = np.c_[xy, np.ones(len(xy))]
        return (np.linalg.inv(self.matrix) @ h.T).T[:, :2]

    def residuals(self, src: np.ndarray, dst: np.ndarray) -> np.ndarray:
        """Per-point euclidean error of forward(src) against dst."""
        return np.linalg.norm(self.forward(src) - np.asarray(dst, dtype=float), axis=1)

    def to_dict(self) -> dict:
        return {"matrix": self.matrix.tolist()}

    @classmethod
    def from_dict(cls, d: dict) -> "Affine":
        return cls(np.asarray(d["matrix"], dtype=float))


@dataclass(frozen=True)
class Similarity(Affine):
    """2-D similarity transform  y = s R x + t: rotation, one scale and translation."""
    kind = "similarity"

    @classmethod
    def fit(cls, src: np.ndarray, dst: np.ndarray) -> "Similarity":
        """Least-squares non-reflective similarity mapping src -> dst, both (N, 2)."""
        src, dst = _check_pairs(src, dst, 2)
        # Unknowns [a, b, tx, ty] with  x' = a x - b y + tx ;  y' = b x + a y + ty
        n = len(src)
        A = np.zeros((2 * n, 4))
        A[0::2, 0] = src[:, 0]
        A[0::2, 1] = -src[:, 1]
        A[0::2, 2] = 1
        A[1::2, 0] = src[:, 1]
        A[1::2, 1] = src[:, 0]
        A[1::2, 3] = 1
        rhs = dst.reshape(-1)
        a, b, tx, ty = np.linalg.lstsq(A, rhs, rcond=None)[0]
        return cls(np.array([[a, -b, tx], [b, a, ty], [0.0, 0.0, 1.0]]))


def _monomials(xy: np.ndarray, order: int) -> np.ndarray:
    x, y = xy[:, 0], xy[:, 1]
    return np.column_stack([x ** i * y ** j for n in range(order + 1) for i in range(n, -1, -1) for j in [n - i]])


@dataclass(frozen=True)
class Polynomial:
    """2-D polynomial transform of order 2 or 3, for optical distortion an affine cannot follow. Each direction is
    fitted by least squares on its own (``forward`` src -> dst, ``inverse`` dst -> src), as a polynomial has no
    closed-form inverse. ``linear`` is the affine fit of the same points, for its scale and rotation."""
    order: int
    forward_coef: np.ndarray   # (terms, 2)
    inverse_coef: np.ndarray
    linear: Affine

    @property
    def kind(self) -> str:
        return f"polynomial{self.order}"

    @classmethod
    def fit(cls, src: np.ndarray, dst: np.ndarray, order: int = 2) -> "Polynomial":
        terms = (order + 1) * (order + 2) // 2
        src, dst = _check_pairs(src, dst, terms)
        fwd = np.linalg.lstsq(_monomials(src, order), dst, rcond=None)[0]
        inv = np.linalg.lstsq(_monomials(dst, order), src, rcond=None)[0]
        return cls(order, fwd, inv, Affine.fit(src, dst))

    @property
    def scale(self) -> float:
        return self.linear.scale

    @property
    def rotation_deg(self) -> float:
        return self.linear.rotation_deg

    def forward(self, xy: np.ndarray) -> np.ndarray:
        return _monomials(np.atleast_2d(np.asarray(xy, dtype=float)), self.order) @ self.forward_coef

    def inverse(self, xy: np.ndarray) -> np.ndarray:
        return _monomials(np.atleast_2d(np.asarray(xy, dtype=float)), self.order) @ self.inverse_coef

    def residuals(self, src: np.ndarray, dst: np.ndarray) -> np.ndarray:
        return np.linalg.norm(self.forward(src) - np.asarray(dst, dtype=float), axis=1)


# The lab's MATLAB Zapit fix of 2026-10-26/27 (zapit.utils.fitScannerTransform), ported: (name, fit, fewest points
# a fit with one point left out needs). On the rig an affine fit was 3.6 to 13 times better than a similarity.
SCANNER_MODELS = (("similarity", Similarity.fit, 3), ("affine", Affine.fit, 3),
                  ("polynomial2", lambda s, d: Polynomial.fit(s, d, 2), 12),
                  ("polynomial3", lambda s, d: Polynomial.fit(s, d, 3), 20))
MAX_DEVIATION_FROM_AFFINE_MM = 1.0   # a polynomial straying further anywhere in the image is extrapolating
SIMPLER_MODEL_MARGIN = 1.1           # a more flexible model must beat a simpler one by more than 10%
OUTLIER_FACTOR, OUTLIER_FLOOR_MM = 4.0, 0.3


@dataclass
class FitInfo:
    """How ``select_scanner_transform`` chose: leave-one-out RMS pointing error per model (inf when not tried),
    which polynomials were refused as unreliable away from the points, the points used and the outliers dropped."""
    chosen: str
    loo_rms_um: dict
    refused: list
    n_points: int
    n_outliers: int
    residuals_um: np.ndarray

    def table(self) -> str:
        lines = [f"Leave-one-out pointing error over {self.n_points} points"
                 + (f" ({self.n_outliers} disagreeing points ignored)" if self.n_outliers else "") + ":"]
        for name, rms in self.loo_rms_um.items():
            note = "  <- used" if name == self.chosen else (
                "  (unreliable away from the calibration points: not used)" if name in self.refused else "")
            lines.append(f"  {name:<12} RMS {rms:7.1f} um{note}" if np.isfinite(rms) else
                         f"  {name:<12} too few points")
        return "\n".join(lines)


def select_scanner_transform(target: np.ndarray, actual: np.ndarray,
                             field_of_view: tuple[float, float, float, float] | None = None):
    """The scanner transform (target, the commanded mm, -> actual, where the camera saw the beam) chosen by
    leave-one-out pointing error, as the lab's MATLAB fix chooses it: points far off an affine fit are dropped
    (more than 4 times the median residual and 0.3 mm), a polynomial departing from the affine by more than 1 mm
    anywhere in ``field_of_view`` (x0, x1, y0, y1 mm) is refused, and the simplest model within 10% of the best is
    used. Errors are measured as the beam is aimed: through ``inverse``. Returns (transform, FitInfo)."""
    target, actual = _check_pairs(target, actual, 2)
    outlier = np.zeros(len(target), dtype=bool)
    if len(target) >= 6:
        res = np.linalg.norm(Affine.fit(target, actual).inverse(actual) - target, axis=1)
        outlier = res > max(OUTLIER_FACTOR * np.median(res), OUTLIER_FLOOR_MM)
        if np.sum(~outlier) < 6:
            outlier[:] = False
    target, actual = target[~outlier], actual[~outlier]
    n = len(target)
    if field_of_view is None:
        field_of_view = (actual[:, 0].min(), actual[:, 0].max(), actual[:, 1].min(), actual[:, 1].max())

    loo, fits = {}, {}
    for name, fit, needs in SCANNER_MODELS:
        loo[name] = np.inf
        if n - 1 < needs:
            continue
        err = np.empty(n)
        try:
            for j in range(n):
                keep = np.arange(n) != j
                err[j] = np.linalg.norm(fit(target[keep], actual[keep]).inverse(actual[j:j + 1])[0] - target[j])
            fits[name] = fit(target, actual)
        except (np.linalg.LinAlgError, ValueError):
            continue
        loo[name] = float(np.sqrt(np.mean(err ** 2)) * 1e3)

    refused = []
    if "affine" in fits:
        x0, x1, y0, y1 = field_of_view
        gx, gy = np.meshgrid(np.linspace(x0, x1, 25), np.linspace(y0, y1, 25))
        lattice = np.c_[gx.ravel(), gy.ravel()]
        on_affine = fits["affine"].inverse(lattice)
        for name in ("polynomial2", "polynomial3"):
            if name in fits and np.linalg.norm(fits[name].inverse(lattice) - on_affine, axis=1).max() > \
                    MAX_DEVIATION_FROM_AFFINE_MM:
                refused.append(name)
    usable = {k: (np.inf if k in refused else v) for k, v in loo.items()}
    best = min(usable.values())
    chosen = "similarity" if not np.isfinite(best) else next(k for k, v in usable.items()
                                                             if v <= best * SIMPLER_MODEL_MARGIN)
    tf = fits.get(chosen) or Similarity.fit(target, actual)
    residuals = np.linalg.norm(tf.inverse(actual) - target, axis=1) * 1e3
    return tf, FitInfo(chosen, loo, refused, n, int(outlier.sum()), residuals)


# --------------------------------------------------------------------------- sample mm / pixels -> volts
class ScannerMapping:
    """Convert positions in the camera image into scanner voltages.

    Holds the camera scale, the nominal volts-per-pixel and the (optional) fitted
    scanner calibration.
    """

    def __init__(self, microns_per_pixel: float, volts_per_pixel: float,
                 invert_x: bool = False, invert_y: bool = False,
                 transform: Affine | Polynomial | None = None):
        self.microns_per_pixel = float(microns_per_pixel)
        self.volts_per_pixel = float(volts_per_pixel)
        self.invert_x = bool(invert_x)
        self.invert_y = bool(invert_y)
        self.transform = transform

    @property
    def mm_per_pixel(self) -> float:
        return self.microns_per_pixel * 1e-3

    def mm_to_volts(self, xy_mm: np.ndarray) -> np.ndarray:
        """Sample-space mm (origin image centre) -> (N, 2) scanner volts."""
        xy = np.atleast_2d(np.asarray(xy_mm, dtype=float))
        if self.transform is not None:
            xy = self.transform.inverse(xy)
        volts = xy * (self.volts_per_pixel / self.mm_per_pixel)
        if self.invert_x:
            volts[:, 0] *= -1
        if self.invert_y:
            volts[:, 1] *= -1
        return volts

    def volts_to_mm(self, volts: np.ndarray) -> np.ndarray:
        """Inverse of mm_to_volts (used by the simulated camera and for display)."""
        v = np.atleast_2d(np.asarray(volts, dtype=float)).copy()
        if self.invert_x:
            v[:, 0] *= -1
        if self.invert_y:
            v[:, 1] *= -1
        xy = v / (self.volts_per_pixel / self.mm_per_pixel)
        if self.transform is not None:
            xy = self.transform.forward(xy)
        return xy

    def pixels_to_mm(self, xy_px: np.ndarray, image_size: tuple[int, int]) -> np.ndarray:
        """Pixel coords (col, row) -> sample mm with the origin at the image centre."""
        xy = np.atleast_2d(np.asarray(xy_px, dtype=float))
        centre = np.asarray(image_size, dtype=float) / 2
        return (xy - centre) * self.mm_per_pixel

    def mm_to_pixels(self, xy_mm: np.ndarray, image_size: tuple[int, int]) -> np.ndarray:
        xy = np.atleast_2d(np.asarray(xy_mm, dtype=float))
        centre = np.asarray(image_size, dtype=float) / 2
        return xy / self.mm_per_pixel + centre

    def pixels_to_volts(self, xy_px: np.ndarray, image_size: tuple[int, int]) -> np.ndarray:
        return self.mm_to_volts(self.pixels_to_mm(xy_px, image_size))
