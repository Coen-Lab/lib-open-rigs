"""Sample: two clicks, and registration of a synthetic skull that is rotated, shifted and rescaled."""
import cv2
import numpy as np
import pytest

from open_rigs.zapit import sample as sm
from open_rigs.zapit.bank import sample_hash
from open_rigs.zapit.scanner import ScannerSettings

SETTINGS = ScannerSettings(microns_per_pixel=12.0, volts_per_pixel=0.0015, image_size=(800, 600))


def skull(seed=0, size=(800, 600)):
    """A bright textured head on a dark background, with vessels, sutures, specks and a glare patch."""
    rng = np.random.default_rng(seed)
    w, h = size
    img = np.full((h, w), 15, np.uint8)
    cv2.ellipse(img, (w // 2, h // 2), (260, 200), 0, 0, 360, 110, -1)
    noise = cv2.GaussianBlur(rng.normal(0, 1, (h, w)).astype(np.float32), (0, 0), 2.5) * 70
    img = np.clip(img + noise * (img > 100), 0, 255).astype(np.uint8)
    for _ in range(150):
        p = (int(rng.integers(w // 2 - 230, w // 2 + 230)), int(rng.integers(h // 2 - 170, h // 2 + 170)))
        cv2.circle(img, p, int(rng.integers(2, 6)), int(rng.integers(40, 250)), -1)
        q = (p[0] + int(rng.integers(-40, 40)), p[1] + int(rng.integers(-40, 40)))
        cv2.line(img, p, q, int(rng.integers(40, 250)), 2)
    cv2.circle(img, (w // 2 + 120, h // 2 - 90), 14, 255, -1)    # glare
    return img


def move(img, angle_deg, shift, scale=1.0):
    h, w = img.shape
    m = cv2.getRotationMatrix2D((w / 2, h / 2), angle_deg, scale)
    m[:, 2] += shift
    return cv2.warpAffine(img, m, (w, h), borderValue=15), np.vstack([m, [0, 0, 1]])


@pytest.fixture(scope="module")
def reference():
    return skull(), sm.sample_from_clicks([400, 300], [400, 180], SETTINGS)


def test_clicks_give_the_reference_points():
    s = sm.sample_from_clicks([400, 300], [400, 180], SETTINGS)
    np.testing.assert_allclose(s.ref_sample_mm[0], [0, 0], atol=1e-9)    # bregma at the image centre
    np.testing.assert_allclose(s.ref_sample_mm[1], [0, -120 * 0.012], atol=1e-9)
    assert s.ref_stereotaxic[1, 1] == sm.LAMBDA_AP_MM
    assert len(sample_hash(s.ref_stereotaxic, s.ref_sample_mm)) == 64
    assert sm.Sample.from_dict(s.to_dict()).method == "clicks"
    with pytest.raises(ValueError):
        sm.sample_from_clicks([5, 5], [5, 5], SETTINGS)


def test_the_mask_covers_the_head_not_the_background():
    m = sm.skull_mask(skull())
    assert m[300, 400] == 255 and m[5, 5] == 0 and m[210, 520] == 0      # centre in; corner out; glare out


@pytest.mark.parametrize("angle,shift", [(0, (0, 0)), (12, (40, -25)), (-18, (-30, 35))])
def test_registration_recovers_the_move(reference, angle, shift):
    ref_img, ref_sample = reference
    new, m = move(ref_img, angle, shift)
    reg = sm.register_sample(ref_img, ref_sample, new, SETTINGS)
    assert reg.passed, reg.reason
    assert reg.inliers >= sm.MIN_INLIERS and reg.correlation >= sm.MIN_CORRELATION
    assert reg.transform is not None and reg.sample is not None
    np.testing.assert_allclose(reg.transform, m, atol=0.5)
    want = np.c_[ref_sample.ref_px, np.ones(2)] @ m[:2].T
    np.testing.assert_allclose(reg.sample.ref_px, want, atol=1.0)
    assert reg.sample.method == "registered"


def test_registration_scales_by_microns_per_pixel(reference):
    ref_img, ref_sample = reference
    # the new still is at 10 um/px: the same head looks 1.2 times larger, and registration undoes that
    new, m = move(ref_img, 8, (20, 10), scale=ref_sample.microns_per_pixel / 10.0)
    reg = sm.register_sample(ref_img, ref_sample, new, ScannerSettings(10.0, 0.0015, (800, 600)))
    assert reg.passed, reg.reason
    assert reg.transform is not None
    np.testing.assert_allclose(reg.transform, m, atol=0.8)


def test_a_different_head_fails(reference):
    ref_img, ref_sample = reference
    reg = sm.register_sample(ref_img, ref_sample, skull(seed=7), SETTINGS)
    assert not reg.passed and reg.sample is None


def test_a_blank_image_fails_without_raising(reference):
    ref_img, ref_sample = reference
    reg = sm.register_sample(ref_img, ref_sample, np.full((600, 800), 15, np.uint8), SETTINGS)
    assert not reg.passed and reg.reason


def test_the_limits_live_in_one_place():
    assert (sm.MIN_INLIERS, sm.MIN_CORRELATION, sm.MAX_ROTATION_DEG) == (30, 0.6, 20.0)


def test_the_record_carries_the_correlation_and_its_limits(reference):
    ref_img, ref_sample = reference
    d = sm.register_sample(ref_img, ref_sample, move(ref_img, 5, (10, 5))[0], SETTINGS).to_dict()
    assert d["passed"] and d["correlation"] >= 0.6 and abs(d["rotation_deg"] - (-5)) < 0.5
    assert (d["min_correlation"], d["max_rotation_deg"]) == (0.6, 20.0) and "residual_um" not in d
    assert sm.Registration(False, "x", None).to_dict()["correlation"] is None   # json-safe without a match


B2 = ScannerSettings(microns_per_pixel=19.3, volts_per_pixel=0.0015, image_size=(1136, 940))


def b2_still(name):
    from pathlib import Path
    img = cv2.imread(str(Path(__file__).parent / "data" / name), cv2.IMREAD_UNCHANGED)
    assert img is not None
    return sm.grey8(img)


def test_a_real_dim_skull_registers_to_a_later_dimmer_rotated_still_of_itself():
    """Audit 24 Nov 2026: B2's still (mean grey 28) gave ORB 20 corners and never registered, even to itself; the
    contrast-equalised still registers a 4 degree turn, a shift, 15 % less light and noise."""
    ref = b2_still("still_skull_B2.png")
    ref_sample = sm.sample_from_clicks((560, 300), (560, 720), B2)
    h, w = ref.shape
    m = cv2.getRotationMatrix2D((w / 2, h / 2), 4, 1.0)
    m[:, 2] += (25, -15)
    later = cv2.warpAffine(ref, m, (w, h))
    later = np.clip(later * 0.85 + np.random.default_rng(0).normal(0, 3, later.shape), 0, 255).astype(np.uint8)
    reg = sm.register_sample(ref, ref_sample, later, B2)
    assert reg.passed, reg.reason
    assert reg.transform is not None
    np.testing.assert_allclose(reg.transform[:2, :2], m[:, :2], atol=0.01)   # the turn
    np.testing.assert_allclose(reg.transform[:2, 2], m[:, 2], atol=3.0)      # the shift, px


def test_the_same_skull_off_centre_registers_as_a_shift():
    reg = sm.register_sample(b2_still("still_skull_B2.png"), sm.sample_from_clicks((560, 300), (560, 720), B2),
                             b2_still("still_skull_offcentre_B2.png"), B2)
    assert reg.passed, reg.reason
    assert reg.transform is not None
    assert abs(reg.scale - 1) < 0.02 and abs(reg.transform[1, 0]) < 0.01


B2_CLICKS = ((560, 300), (560, 720))   # bregma, and lambda 420 px (8.1 mm) below it


def test_the_real_pair_registers_both_ways_and_the_round_trip_lands_within_25_um():
    """Audit E F3: the free similarity scale (1.019) put lambda 101 um off on the round trip; the rigid refit 19/14."""
    a, b = b2_still("still_skull_B2.png"), b2_still("still_skull_offcentre_B2.png")
    clicks = sm.sample_from_clicks(*B2_CLICKS, B2)
    ab, ba = sm.register_sample(a, clicks, b, B2), sm.register_sample(b, clicks, a, B2)
    assert ab.passed and ba.passed, (ab.reason, ba.reason)
    assert min(ab.correlation, ba.correlation) >= 0.85
    assert ab.transform is not None and ba.transform is not None
    np.testing.assert_allclose(np.linalg.det(ab.transform[:2, :2]), 1.0, atol=1e-9)   # rigid: no scale
    pts = np.c_[np.array(B2_CLICKS, float), np.ones(2)]
    back = pts @ (ab.transform @ ba.transform)[:2].T
    err_um = np.linalg.norm(back - pts[:, :2], axis=1).max() * B2.microns_per_pixel
    assert err_um < 25, (err_um, [(r.inliers, r.correlation, r.rotation_deg, r.scale, r.transform) for r in (ab, ba)])


def _same_plate(skull_inside):
    """B2's still with the skull (find_target's region) replaced by `skull_inside`: the same plate, another skull."""
    from open_rigs.zapit.calibration import find_target
    ref = b2_still("still_skull_B2.png")
    inside, _ = find_target(ref)
    out = ref.copy()
    out[inside] = skull_inside(ref, inside)[inside]
    return out


def _turned_180(ref, inside):
    cy, cx = np.argwhere(inside).mean(0)
    return cv2.warpAffine(ref, cv2.getRotationMatrix2D((cx, cy), 180, 1.0), ref.shape[::-1])


WRONG = {"skull turned 180 deg in the plate": lambda: _same_plate(_turned_180),
         "skull mirrored in the plate": lambda: _same_plate(lambda ref, _: cv2.flip(ref, 1)),
         "mirrored": lambda: cv2.flip(b2_still("still_skull_B2.png"), 1),
         "off-centre mirrored": lambda: cv2.flip(b2_still("still_skull_offcentre_B2.png"), 1),
         "noise": lambda: np.clip(np.random.default_rng(5).normal(28, 10, (940, 1136)), 0, 255).astype(np.uint8),
         "synthetic skull": lambda: skull(3, (1136, 940))}


@pytest.mark.parametrize("name", WRONG)
def test_wrong_pairs_fail_both_ways(name):
    """Audit E's false positives: the skull turned 180 deg inside an unmoved plate passed with 362 inliers before
    the rotation limit."""
    ref, other = b2_still("still_skull_B2.png"), WRONG[name]()
    clicks = sm.sample_from_clicks(*B2_CLICKS, B2)
    for a, b in ((ref, other), (other, ref)):
        reg = sm.register_sample(a, clicks, b, B2)
        assert not reg.passed and reg.sample is None and reg.reason


def test_the_correlation_tells_the_same_skull_from_another():
    ref = b2_still("still_skull_B2.png")
    eq, mask = cv2.createCLAHE(sm.CLAHE_CLIP, (8, 8)).apply(ref), sm.skull_mask(ref)
    same = sm.correlation(eq, mask, eq, mask, np.eye(2, 3))
    other = sm.correlation(eq, mask, cv2.flip(eq, 1), cv2.flip(mask, 1), np.eye(2, 3))
    assert same > 0.99 and other < sm.MIN_CORRELATION
