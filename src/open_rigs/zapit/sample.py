"""The sample: bregma and a second midline point from two clicks, or registration of the mouse's last reference still.

The two points are atlas coordinates [ML, AP] in mm: bregma [0, 0] and the caller's second point [0, ap] on the midline
(MATLAB Zapit's refAP point, pointer.m refPointsStereotaxic = [0 0; 0 refAP], +AP anterior; the rig app gives it,
3 mm posterior on 27 Nov 2026). They set the
conversion from atlas units to the still (position, rotation and scale), so the second point's distance is in atlas mm,
scaled to each mouse by where it is clicked, not a skull landmark's (lambda is a Paxinos skull landmark the Allen atlas
does not define; 27 Nov 2026).

Registration scales the reference to the new image's micronsPerPixel, masks the skull and head-plate, matches ORB
features, finds the consistent ones with a RANSAC similarity and refits them as rigid (rotation and shift: the scale
is known from micronsPerPixel, and a free one put a point 100 um off on B2's real pair, audit E F3). The similarity's
scale is kept only as the not-the-same-head check. It passes on enough inliers, a small turn and the masked
correlation of the equalised stills after the move; the limits, and the other tunables, are the constants below.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import cast

import numpy as np

from open_rigs.zapit.scanner import ScannerSettings

MIN_INLIERS = 30          # a registration needs at least this many RANSAC inliers ...
# ... and the equalised stills, the reference moved onto the new one, must correlate at least this inside both masks.
# B2's real pair 0.91, wrong pairs 0.18-0.23 (audit E); an inlier residual separated nothing (always under RANSAC's).
MIN_CORRELATION = 0.6
MAX_ROTATION_DEG = 20.0   # a head-fixed mouse turns a few degrees between sessions; the skull turned 180 deg passed
MIN_OVERLAP_PX = 1000     # fewer masked pixels in common than this is no overlap to correlate
CORRELATION_BLUR_PX = 2.0
RANSAC_THRESHOLD_UM = 60.0
SCALE_TOLERANCE = 0.1     # a similarity scaling the head by more than this fraction is not the same head
ORB_FEATURES = 2000
# Dim stills: ORB runs on the contrast-equalised (CLAHE) still with a low corner threshold. OpenCV's defaults found
# 20 corners on B2's skull (mean grey 28 of 255), which never registered, even to itself (audit 24 Nov 2026).
ORB_FAST_THRESHOLD = 5
CLAHE_CLIP = 3.0
MASK_ERODE_FRACTION = 0.03   # the mask is pulled in from the skull's edge by this fraction of the image width
SATURATED = 250           # glare at or above this grey level (8 bit) is masked out


@dataclass
class Sample:
    """The two reference points in stereotaxic mm and as seen in the image (sample mm, as ``ScannerMapping`` gives
    them), with the pixels they were clicked at, so a later registration can carry them across."""
    ref_stereotaxic: np.ndarray          # (2, 2) [ML, AP]: bregma, the second point
    ref_sample_mm: np.ndarray            # (2, 2)
    ref_px: np.ndarray                   # (2, 2) [col, row] in the image
    microns_per_pixel: float
    image_size: tuple[int, int]
    method: str = "clicks"               # "clicks" or "registered"

    def to_dict(self) -> dict:
        return {"method": self.method, "ref_stereotaxic": np.asarray(self.ref_stereotaxic).tolist(),
                "ref_sample_mm": np.asarray(self.ref_sample_mm).tolist(), "ref_px": np.asarray(self.ref_px).tolist(),
                "microns_per_pixel": self.microns_per_pixel, "image_size": list(self.image_size)}

    @classmethod
    def from_dict(cls, d: dict) -> "Sample":
        return cls(np.array(d["ref_stereotaxic"], float), np.array(d["ref_sample_mm"], float),
                   np.array(d["ref_px"], float), float(d["microns_per_pixel"]), (int(d["image_size"][0]), int(d["image_size"][1])), d["method"])


def sample_from_clicks(bregma_px, second_px, settings: ScannerSettings, second_ap_mm: float) -> Sample:
    """The sample from clicks on the live tile: bregma, then the midline point at atlas AP `second_ap_mm` (the
    caller's one constant; no default here, so no second copy of it), as (col, row) pixels of the saved still."""
    if not (np.isfinite(second_ap_mm) and second_ap_mm != 0):
        raise ValueError(f"The second point's AP must be a number of mm other than 0, not {second_ap_mm!r}")
    px = np.array([bregma_px, second_px], dtype=float).reshape(2, 2)
    if np.allclose(px[0], px[1]):
        raise ValueError("Bregma and the second point were clicked at the same place")
    return Sample(np.array([[0.0, 0.0], [0.0, float(second_ap_mm)]]), settings.pixels_to_mm(px), px,
                  settings.microns_per_pixel, settings.image_size)


# ------------------------------------------------------------------------------------------ registration
@dataclass
class Registration:
    passed: bool
    reason: str                          # why not, or what was found
    transform: np.ndarray | None         # (3, 3): reference image px -> new image px (micronsPerPixel scale, then rigid)
    inliers: int = 0
    score: float = 0.0                   # the inlier count
    correlation: float = float("nan")    # masked correlation of the equalised stills after the move
    scale: float = float("nan")          # the RANSAC similarity's scale: the not-the-same-head check only
    rotation_deg: float = float("nan")
    sample: Sample | None = None         # the reference's points carried into the new image; only when passed

    def to_dict(self) -> dict:
        def num(x):
            return float(x) if np.isfinite(x) else None
        return {"passed": self.passed, "reason": self.reason, "inliers": self.inliers,
                "correlation": num(self.correlation), "scale": num(self.scale), "rotation_deg": num(self.rotation_deg),
                "transform": None if self.transform is None else self.transform.tolist(), "min_inliers": MIN_INLIERS,
                "min_correlation": MIN_CORRELATION, "max_rotation_deg": MAX_ROTATION_DEG,
                "scale_tolerance": SCALE_TOLERANCE}


def grey8(img: np.ndarray) -> np.ndarray:
    a = np.asarray(img)
    if a.ndim == 3:
        a = a[..., 0]
    if a.dtype != np.uint8:
        top = 65535.0 if a.dtype == np.uint16 else max(float(a.max()), 1.0)
        a = np.clip(a.astype(np.float64) * (255.0 / top), 0, 255)
    return a.astype(np.uint8)


def skull_mask(img: np.ndarray) -> np.ndarray:
    """uint8 mask (255 inside) of the head and plate: the largest bright region (Otsu), holes filled, pulled in from
    its edge, with glare removed. The background and anything off the animal never supply features."""
    import cv2
    g = grey8(img)
    blur = cv2.GaussianBlur(g, (0, 0), 3)
    _, bw = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    n, labels, stats, _ = cv2.connectedComponentsWithStats(bw)
    if n < 2:
        return np.zeros_like(g)
    keep = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    mask = (labels == keep).astype(np.uint8) * 255
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    cv2.drawContours(mask, contours, -1, 255, cv2.FILLED)
    k = max(1, int(g.shape[1] * MASK_ERODE_FRACTION))
    mask = cv2.erode(mask, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * k + 1, 2 * k + 1)))
    mask[g >= SATURATED] = 0
    return mask


def _fail(reason, **kw) -> Registration:
    return Registration(False, reason, kw.pop("transform", None), **kw)


def _rigid(src: np.ndarray, dst: np.ndarray) -> np.ndarray:
    """(2, 3) rotation and shift taking src to dst in least squares (Kabsch)."""
    sc, dc = src.mean(0), dst.mean(0)
    u, _, vt = np.linalg.svd((src - sc).T @ (dst - dc))
    r = (u @ np.diag([1.0, np.sign(np.linalg.det(u @ vt))]) @ vt).T
    return np.c_[r, dc - r @ sc]


def correlation(ref: np.ndarray, ref_mask: np.ndarray, new: np.ndarray, new_mask: np.ndarray, m: np.ndarray) -> float:
    """Correlation of ``ref`` moved by the (2, 3) ``m`` onto ``new``, over the pixels inside both masks after a light
    blur; nan when they share fewer than MIN_OVERLAP_PX or one side is flat."""
    import cv2
    h, w = new.shape
    moved = cv2.warpAffine(ref.astype(np.float32), m, (w, h))
    both = (cv2.warpAffine(ref_mask, m, (w, h), flags=cv2.INTER_NEAREST) > 0) & (new_mask > 0)
    if both.sum() < MIN_OVERLAP_PX:
        return float("nan")
    x = cv2.GaussianBlur(moved, (0, 0), CORRELATION_BLUR_PX)[both]
    y = cv2.GaussianBlur(new.astype(np.float32), (0, 0), CORRELATION_BLUR_PX)[both]
    if x.std() == 0 or y.std() == 0:
        return float("nan")
    return float(np.corrcoef(x, y)[0, 1])


def register_sample(reference_image: np.ndarray, reference: Sample, image: np.ndarray,
                    settings: ScannerSettings) -> Registration:
    """Register ``image`` (the new still, at ``settings``) to ``reference_image`` (the mouse's last accepted still,
    described by ``reference``). Returns the move from reference pixels to new pixels, the inlier count as the score
    and pass or fail; on a pass, ``sample`` carries the reference's two points into the new image."""
    import cv2
    s = reference.microns_per_pixel / settings.microns_per_pixel      # reference px -> new px scale
    ref = grey8(reference_image)
    ref_scaled = cv2.resize(ref, None, fx=s, fy=s, interpolation=cv2.INTER_AREA if s < 1 else cv2.INTER_CUBIC)
    new = grey8(image)
    mr, mn = skull_mask(ref_scaled), skull_mask(new)
    if not mr.any() or not mn.any():
        return _fail("No skull or head-plate could be found in one of the images")
    orb = cv2.ORB.create(nfeatures=ORB_FEATURES, fastThreshold=ORB_FAST_THRESHOLD)
    clahe = cv2.createCLAHE(clipLimit=CLAHE_CLIP, tileGridSize=(8, 8))   # dim stills: features from local contrast
    er, en = clahe.apply(ref_scaled), clahe.apply(new)
    kr, dr = orb.detectAndCompute(er, mr)
    kn, dn = orb.detectAndCompute(en, mn)
    if len(kr) < 3 or len(kn) < 3:   # no keypoints, no descriptors
        return _fail("Too few features on the skull to register")
    matches = cv2.BFMatcher(cv2.NORM_HAMMING, crossCheck=True).match(dr, dn)
    if len(matches) < 3:
        return _fail("Too few features matched")
    src = np.array([kr[m.queryIdx].pt for m in matches], dtype=np.float64)
    dst = np.array([kn[m.trainIdx].pt for m in matches], dtype=np.float64)
    m2, inl = cast("tuple[np.ndarray | None, np.ndarray | None]", cv2.estimateAffinePartial2D(
        src, dst, method=cv2.RANSAC, ransacReprojThreshold=RANSAC_THRESHOLD_UM / settings.microns_per_pixel,
        maxIters=5000, confidence=0.999))   # (None, None) when no model is found; the stubs omit it
    if m2 is None or inl is None:
        return _fail("No consistent match between the images")
    inl = inl.ravel().astype(bool)
    n_in = int(inl.sum())
    scale = float(np.hypot(m2[0, 0], m2[1, 0]))
    rigid = _rigid(src[inl], dst[inl]) if n_in >= 2 else m2
    rotation = float(np.degrees(np.arctan2(rigid[1, 0], rigid[0, 0])))
    full = np.vstack([rigid, [0, 0, 1]]) @ np.diag([s, s, 1.0])       # reference px -> scaled -> new px
    out = dict(transform=full, inliers=n_in, score=float(n_in), scale=scale, rotation_deg=rotation)
    if n_in < MIN_INLIERS:
        return _fail(f"Only {n_in} matching features, at least {MIN_INLIERS} are needed", **out)
    if abs(scale - 1) > SCALE_TOLERANCE:
        return _fail(f"The match rescales the head by {scale:.2f}, so it is not the same head", **out)
    if abs(rotation) > MAX_ROTATION_DEG:
        return _fail(f"The match turns the head {abs(rotation):.0f} deg, more than {MAX_ROTATION_DEG:.0f}", **out)
    corr = correlation(er, mr, en, mn, rigid)
    out["correlation"] = corr
    if not corr >= MIN_CORRELATION:   # nan: no overlap
        return _fail(f"The stills correlate {corr:.2f} after the match, under {MIN_CORRELATION:g}"
                     if np.isfinite(corr) else "The stills do not overlap after the match", **out)
    pts = np.c_[reference.ref_px, np.ones(2)] @ full[:2].T
    moved = Sample(reference.ref_stereotaxic, settings.pixels_to_mm(pts), pts, settings.microns_per_pixel,
                   settings.image_size, "registered")
    return Registration(True, f"{n_in} features matched, correlation {corr:.2f}", full, n_in, float(n_in), corr,
                        scale, rotation, moved)
