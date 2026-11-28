"""Per-session scanner calibration from saved frames: a simulated rig whose galvos have a known affine error renders
one frame per point; the fit recovers it, skips glints and a far spot, and Measure Accuracy sees the between points."""
import json

import numpy as np
import pytest

from open_rigs.zapit import calibration as calib, scanner as sc
from open_rigs.zapit.laser import LaserCalibration
from open_rigs.zapit.safety import laser_volts, max_control_volts
from open_rigs.zapit.transforms import Affine

TRUE = Affine(np.array([[1.04, 0.02, 0.25], [-0.015, 0.97, -0.2], [0, 0, 1]]))
SETTINGS = sc.ScannerSettings(microns_per_pixel=12.0, volts_per_pixel=0.0015, image_size=(900, 700), invert_x=True)
LASER = LaserCalibration([0.0, 5.0], [0.0, 50.0])
RNG = np.random.default_rng(3)


def spot(shape, px, amp=200.0, sigma=3.0):
    yy, xx = np.mgrid[:shape[0], :shape[1]]
    return amp * np.exp(-((xx - px[0]) ** 2 + (yy - px[1]) ** 2) / (2 * sigma ** 2))


def render(plan, settings=SETTINGS, glint=True, far=(), dark=(), jitter_mm=0.0):
    """Frames for ``plan``: the beam lands where the true galvo error puts it; a static glint sits in every frame."""
    w, h = settings.image_size
    base = np.full((h, w), 20.0)
    if glint:
        base += spot(base.shape, (150, 120), amp=120.0)
    nominal = settings.nominal_mapping()
    frames = {}
    for i, v in enumerate(plan.volts):
        if i in dark:
            continue
        actual = TRUE.forward(nominal.volts_to_mm(v[None]))[0]
        if i in far:
            actual = actual + 3.5
        actual = actual + RNG.normal(0, jitter_mm, 2) if jitter_mm else actual
        px = nominal.mm_to_pixels(actual - np.asarray(settings.roi_offset_mm), settings.image_size)[0]
        frames[i] = np.clip(base + spot(base.shape, px) + RNG.normal(0, 1.0, base.shape), 0, 255).astype(np.uint8)
    off = [np.clip(base + RNG.normal(0, 1.0, base.shape), 0, 255).astype(np.uint8) for _ in range(sc.N_BACKGROUND_FRAMES)]
    return frames, off


@pytest.fixture(scope="module")
def plan():
    return sc.calibration_plan(SETTINGS, LASER, ceiling_mw=1e9)


@pytest.fixture(scope="module")
def run(plan):
    return render(plan)


def test_plan_is_a_grid_with_capped_laser_volts(plan):
    assert len(plan) >= 12 and plan.volts.shape == (len(plan), 2)
    assert plan.laser_volts == pytest.approx(LASER.mw_to_control(2.0))   # the fit fires at 2 mW by default
    assert plan.laser_volts > 0
    low = sc.calibration_plan(SETTINGS, LASER, ceiling_mw=1.0)
    assert low.laser_volts == pytest.approx(LASER.mw_to_control(1.0))
    assert low.laser_volts < plan.laser_volts


def test_a_zero_ceiling_is_0_v_even_above_a_laser_minimum_voltage():
    laser = LaserCalibration([0.5, 5.0], [0.0, 75.0])
    assert sc.calibration_plan(SETTINGS, laser, ceiling_mw=0).laser_volts == 0.0
    assert laser_volts(laser, 10.0, 0) == 0.0 and max_control_volts(laser, 0) == 0.0


def test_a_small_grid_is_refused():
    tiny = sc.ScannerSettings(12.0, 0.0015, (150, 150))
    with pytest.raises(ValueError, match="at least 4"):
        sc.calibration_plan(tiny, LASER, 1e9)


def test_fit_recovers_the_galvo_error_and_aims_true(plan, run):
    frames, off = run
    fit = sc.fit_scanner(plan, frames, off, SETTINGS)
    assert fit.ok and fit.n_found == len(plan) and fit.mean_residual_um < 60
    assert fit.mapping is not None and fit.info is not None
    assert fit.info.chosen in ("affine", "similarity", "polynomial2")
    aimed = fit.mapping.mm_to_volts(plan.points_mm)
    nominal = SETTINGS.nominal_mapping()
    landed = TRUE.forward(nominal.volts_to_mm(aimed))
    assert np.abs(landed - plan.points_mm).max() < 0.1


def test_far_spots_and_missing_frames_are_skipped(plan):
    frames, off = render(plan, far=(0,), dark=(5,))
    fit = sc.fit_scanner(plan, frames, off, SETTINGS)
    assert fit.ok and fit.n_found == len(plan) - 2
    assert fit.misses == {"spot too far from where the beam was aimed": 1, sc.MISS_FRAME: 1}
    assert fit.map is not None and fit.map.missed_mm.shape == (2, 2)


def test_too_few_points_refuse(plan):
    frames, off = render(plan, dark=tuple(range(2, len(plan))))
    fit = sc.fit_scanner(plan, frames, off, SETTINGS)
    assert not fit.ok and fit.mapping is None and "at least 4" in fit.report


def test_a_poor_fit_is_refused(plan):
    frames, off = render(plan, jitter_mm=0.6)    # every spot 0.6 mm off: nothing the fit can follow
    fit = sc.fit_scanner(plan, frames, off, SETTINGS)
    assert not fit.ok and fit.mapping is None and "the fit is poor" in fit.report


def test_frame_folder_round_trip(tmp_path, plan, run):
    sc.save_frames(tmp_path, *run)
    assert (tmp_path / "point_0000.png").exists() and (tmp_path / "background_3.png").exists()
    fit = sc.fit_scanner_run(plan, tmp_path, SETTINGS)
    assert fit.ok and fit.n_found == len(plan)
    assert fit.to_dict()["fit"] is not None


@pytest.mark.parametrize("model", ["affine", "polynomial2"])
def test_the_filed_record_is_strict_json_and_rebuilds_the_mapping_the_bank_hashed(plan, run, model):
    from open_rigs.zapit.bank import scanner_fit_hash
    from open_rigs.zapit.transforms import Polynomial
    frames, off = run
    fit = sc.fit_scanner(plan, frames, off, SETTINGS)
    if model == "polynomial2":  # a polynomial's coefficients are arrays: the record was not JSON (round 3 audit)
        tf = Polynomial.fit(plan.points_mm, TRUE.forward(plan.points_mm), 2)
        fit.transform, fit.mapping = tf, SETTINGS.nominal_mapping(tf)
    record = json.loads(json.dumps(fit.to_dict(), allow_nan=False))  # no NaN or Infinity: strict JSON
    rebuilt = sc.mapping_from_record(record)
    assert fit.mapping is not None
    assert record["scanner_fit_hash"] == scanner_fit_hash(fit.mapping) == scanner_fit_hash(rebuilt)
    assert np.allclose(rebuilt.mm_to_volts(plan.points_mm), fit.mapping.mm_to_volts(plan.points_mm))


def test_measure_accuracy_between_grid_points(plan, run):
    frames, off = run
    fit = sc.fit_scanner(plan, frames, off, SETTINGS)
    aplan = sc.accuracy_plan(SETTINGS, fit, LASER, 1e9)
    assert len(aplan) >= 4
    assert not any(np.allclose(p, g) for p in aplan.points_mm for g in plan.points_mm)
    frames, off = render(aplan)
    acc = sc.measure_accuracy(aplan, frames, off, SETTINGS)
    assert acc.n_found == len(aplan) and acc.mean_um < 150
    assert acc.max_um >= acc.mean_um


def test_accuracy_plan_needs_an_accepted_fit(plan):
    with pytest.raises(ValueError, match="accepted"):
        sc.accuracy_plan(SETTINGS, sc.ScannerFit(False, "x", None), LASER, 1e9)


def test_settings_from_the_rig_block():
    from open_rigs.zapit.rig_config import ZapitRig
    from test_zapit_rig_config import BLOCK
    s = sc.ScannerSettings.from_rig(ZapitRig.from_dict(BLOCK), (900, 700), calibration_power_mw=5.0)
    assert s.invert_x and not s.invert_y and s.microns_per_pixel == 11.7 and s.calibration_power_mw == 5.0


def test_a_record_without_a_residual_is_strict_json():
    # The nominal fit taken at ceilingMw 0 (no beam) has no residual, and an empty accuracy no error: null, not NaN.
    fit = sc.ScannerFit(True, "nominal", SETTINGS.nominal_mapping())
    record = json.loads(json.dumps(fit.to_dict(), allow_nan=False))
    assert record["mean_residual_um"] is None
    from open_rigs.zapit.bank import scanner_fit_hash
    assert scanner_fit_hash(sc.mapping_from_record(record)) == record["scanner_fit_hash"]
    empty = sc.Accuracy(0, 4, np.array([]), {}, calib.CalibrationMap(np.empty((0, 2)), np.empty(0), np.empty((0, 2))))
    assert json.loads(json.dumps(empty.to_dict(), allow_nan=False))["mean_um"] is None
