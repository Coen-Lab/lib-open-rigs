"""The grid kept on the target: the bright region of the still (the skull inside the headplate ring, or a card), less
the buffer, found by itself, or a rectangle a person drew; aimed true when the target is off the image centre and the
ROI off the sensor centre."""
from dataclasses import replace
from pathlib import Path

import cv2
import numpy as np
import pytest

from open_rigs.zapit import calibration as calib, scanner as sc
from test_zapit_scanner_session import LASER, TRUE, render

DATA = Path(__file__).parent / "data"
SIZE = (900, 700)
SETTINGS = sc.ScannerSettings(microns_per_pixel=40.0, volts_per_pixel=0.005, image_size=SIZE, invert_x=True,
                              roi_offset_mm=(1.3, -0.8))


def still(discs=((620, 330, 190),), ring=True, level=90.0):
    """A dark still with bright discs (col, row, radius) and a thin bright arc, like the headplate ring's edge."""
    w, h = SIZE
    yy, xx = np.mgrid[:h, :w]
    img = np.full((h, w), 12.0)
    for c, r, rad in discs:
        img[(xx - c) ** 2 + (yy - r) ** 2 < rad ** 2] = level
    if ring:
        d = np.hypot(xx - 450, yy - 350)
        img[(d > 330) & (d < 345) & (yy > 350)] = 200.0
    return np.clip(img + np.random.default_rng(1).normal(0, 3, img.shape), 0, 255).astype(np.uint8)


def pixels(settings, mm):
    return settings.nominal_mapping().mm_to_pixels(np.asarray(mm) - np.asarray(settings.roi_offset_mm), settings.image_size)


def test_the_target_is_the_bright_disc_not_the_ring():
    mask, how = calib.find_target(still())
    assert mask is not None and "bright region" in how
    assert mask[330, 620] and not mask[350 + 337, 450] and not mask[50, 50]


def test_an_off_centre_target_with_an_roi_offset_is_aimed_where_it_is():
    settings, why = sc.with_found_area(SETTINGS, still())
    assert settings is not None and "grid points" in why
    plan = sc.calibration_plan(settings, LASER, 1e9)
    px = pixels(settings, plan.points_mm)
    buffer_px = settings.buffer_mm * 1e3 / settings.microns_per_pixel
    assert len(plan) >= sc.MIN_POINTS
    assert (np.hypot(px[:, 0] - 620, px[:, 1] - 330) <= 190 - buffer_px + 8).all()   # on the disc less the buffer (found at a quarter scale)
    assert np.allclose(plan.volts, settings.nominal_mapping().mm_to_volts(plan.points_mm))
    frames, off = render(plan, settings)
    fit = sc.fit_scanner(plan, frames, off, settings)
    assert fit.ok and fit.n_found == len(plan)
    assert fit.mapping is not None
    landed = TRUE.forward(settings.nominal_mapping().volts_to_mm(fit.mapping.mm_to_volts(plan.points_mm)))
    assert np.abs(landed - plan.points_mm).max() < 0.1
    between = sc.accuracy_points_mm(settings)
    bpx = pixels(settings, between)
    assert len(between) >= 2 and (np.hypot(bpx[:, 0] - 620, bpx[:, 1] - 330) <= 190 - buffer_px + 8).all()
    assert not any(np.allclose(b, g) for b in between for g in plan.points_mm)


def test_the_whole_still_keeps_the_old_grid():
    assert np.allclose(sc.grid_points_mm(SETTINGS), calib.calibration_grid(
        SIZE, 0.040, SETTINGS.point_spacing_mm, SETTINGS.buffer_mm, SETTINGS.roi_offset_mm))


@pytest.mark.parametrize("discs, words", [
    (((250, 350, 150), (650, 350, 150)), "about the same size"),
    (((450, 350, 50),), "under 2%"),
    (((450, 350, 75),), "at least 4 are needed"),
])
def test_an_ambiguous_or_small_target_is_refused(discs, words):
    settings, why = sc.with_found_area(replace(SETTINGS, buffer_mm=2.0), still(discs, ring=False))  # discs sized for 2 mm
    assert settings is None and words in why


def test_a_flat_card_is_the_whole_still():
    mask, how = calib.find_target(np.full(SIZE[::-1], 150, np.uint8))
    assert mask is not None and mask.all() and "whole still" in how


def test_a_drawn_rectangle_holds_the_grid():
    settings, why = sc.with_rectangle_area(SETTINGS, (0.9, 0.85, 0.4, 0.2))   # corners in either order
    assert settings is not None
    px = pixels(settings, sc.grid_points_mm(settings))
    assert (px[:, 0] >= 0.4 * 900).all() and (px[:, 0] <= 0.9 * 900).all()
    assert (px[:, 1] >= 0.2 * 700).all() and (px[:, 1] <= 0.85 * 700).all()
    small, why = sc.with_rectangle_area(SETTINGS, (0.45, 0.45, 0.55, 0.55))
    assert small is None and "at least 4" in why


@pytest.mark.parametrize("name, box", [
    ("still_skull_B2.png", (290, 150, 820, 750)),            # B2, 24 Nov 2026: the skull, ring around it
    ("still_skull_offcentre_B2.png", (540, 160, 1040, 730)),  # B2, 23 Nov 2026: the skull right of centre
])
def test_the_skull_is_found_on_b2_stills(name, box):
    image = cv2.imread(str(DATA / name), cv2.IMREAD_UNCHANGED)
    assert image is not None
    rig = sc.ScannerSettings(19.3, 0.001, (image.shape[1], image.shape[0]))
    settings, why = sc.with_found_area(rig, image)
    assert settings is not None, why
    px = pixels(settings, sc.grid_points_mm(settings))
    x0, y0, x1, y1 = box
    assert len(px) >= 6 and (px[:, 0] > x0).all() and (px[:, 0] < x1).all() and (px[:, 1] > y0).all() and (px[:, 1] < y1).all()


def test_a_small_area_still_gets_enough_accuracy_points_the_fit_never_saw():
    # A 3 x 2 grid has only 2 points diagonally between: half steps along the axes are added up to MIN_POINTS.
    settings, _ = sc.with_rectangle_area(sc.ScannerSettings(12.0, 0.0015, SIZE, buffer_mm=2.0), (0.05, 0.1, 0.95, 0.9))
    assert settings is not None
    grid, between = sc.grid_points_mm(settings), sc.accuracy_points_mm(settings)
    assert len(grid) == 6
    assert len(between) >= sc.MIN_POINTS
    assert not any(np.allclose(b, g) for b in between for g in grid)



def test_accuracy_never_measures_on_the_fits_own_points():
    # A 2 x 2 grid has one point between (audit B, L1): accuracy is refused with the reason, never measured on the grid.
    settings = sc.ScannerSettings(40.0, 0.005, SIZE, point_spacing_mm=20.0, buffer_mm=1.0)
    grid, between = sc.grid_points_mm(settings), sc.accuracy_points_mm(settings)
    assert len(grid) == 4 and len(between) == 1 and not any(np.allclose(b, g) for b in between for g in grid)
    fit = sc.ScannerFit(True, "ok", settings.nominal_mapping())
    with pytest.raises(ValueError, match="at least 2 points the fit never saw, and this grid has 1"):
        sc.accuracy_plan(settings, fit, LASER, 1e9)
