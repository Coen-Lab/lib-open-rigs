import numpy as np
import pytest

from open_rigs.zapit.laser import LaserCalibration, LaserFit


def test_laser_fit_prefers_sigmoid_for_saturating_data(tmp_path):
    x = np.linspace(0, 5, 60)
    y = 0.1 + 3 / (1 + np.exp(-2.5 * (x - 2.5)))
    fit = LaserFit.fit(x, y)
    assert fit.fit_type == "sigmoid"
    fit.save(tmp_path / "laserFit.yml")
    again = LaserFit.load(tmp_path / "laserFit.yml")
    np.testing.assert_allclose(again.fitted_sensor_values, fit.fitted_sensor_values, atol=1e-6)
    cal = LaserCalibration([0, 5], [0, 40], again)
    assert 0 < cal.mw_to_control(20) < 5
    assert cal.mw_to_control(20) == __import__("pytest").approx(2.5, abs=0.05)


@pytest.mark.parametrize("name, curve", [
    ("lasing from 1.5 V", lambda x: np.clip((x - 1.5) / 3.5, 0, None)),
    ("lasing from 0.5 V", lambda x: np.clip((x - 0.5) / 4.5, 0, None)),
    ("saturating at 3 V", lambda x: np.clip(x / 3, None, 1)),
])
def test_threshold_and_saturating_lasers_get_their_power(name, curve, tmp_path):
    # A cubic once sent 0 mW for a 1 mW request on a laser lasing from 0.5 V, and refused one lasing from 1.5 V.
    x = np.arange(0, 5 + 1e-9, 0.025)
    y = curve(x) + np.random.default_rng(0).normal(0, 0.005, len(x))
    fit = LaserFit.fit(x, y)
    assert fit.fit_type == "monotone" and fit.problem() is None, name
    fit.save(tmp_path / "laserFit.yml")
    cal = LaserCalibration([0, 5], [0, 20], LaserFit.load(tmp_path / "laserFit.yml"))
    true_mw = (curve(x) - curve(x).min()) / (curve(x).max() - curve(x).min()) * 20
    for mw in (1, 2, 5, 10, 19):
        delivered = np.interp(cal.mw_to_control(mw, warn=None), x, true_mw)
        assert delivered == pytest.approx(mw, rel=0.2), (name, mw)   # the formula fits were off by 0 to 100%


def delivered_errors(lo, hi, curve, noise, requests=(1, 2, 5, 10, 19), seed=0):
    """Fit a 25 mV sweep of ``curve`` with Gaussian noise; mW delivered minus mW requested, on a 20 mW laser."""
    x = np.arange(lo, hi + 1e-9, 0.025)
    y = curve(x) + np.random.default_rng(seed).normal(0, noise, len(x))
    fit = LaserFit.fit(x, y)
    assert fit.problem() is None
    cal = LaserCalibration([lo, hi], [0, 20], fit)
    true_mw = (curve(x) - curve(x).min()) / (curve(x).max() - curve(x).min()) * 20
    return fit, [float(np.interp(cal.mw_to_control(mw, warn=None), x, true_mw)) - mw for mw in requests]


def test_a_short_sweep_keeps_its_threshold():
    # A 0-0.5 V sweep is 21 points: once a fixed 7-point average rounded its corner off and 2 mW gave 0 mW.
    fit, errors = delivered_errors(0, 0.5, lambda x: np.clip((x - 0.3) / 0.2, 0, None), 0.005)
    assert fit.fit_type == "monotone" and max(abs(e) for e in errors) < 0.3, errors


@pytest.mark.parametrize("name, curve, noise, seed", [
    ("lasing from 1.5 V, 2% noise", lambda x: np.clip((x - 1.5) / 3.5, 0, None), 0.02, 0),
    ("lasing from 1 V, saturating at 4 V, 1% noise", lambda x: np.clip((x - 1) / 3, 0, 1), 0.01, 14),
])
def test_a_noisy_threshold_laser_gets_the_monotone_curve(name, curve, noise, seed):
    # Once a cubic within 1.5x the noise was kept: refused at 2% noise, 0 mW for 1 mW at 1% noise.
    fit, errors = delivered_errors(0, 5, curve, noise, seed=seed)
    assert fit.fit_type == "monotone" and max(abs(e) for e in errors) < 0.5, (name, errors)


def test_the_monotone_curve_ends_are_not_pulled_in():
    # Padding the average with the end reading once added 0.15 mW at the low end of a saturating laser.
    _, errors = delivered_errors(0, 5, lambda x: np.clip(x / 3, None, 1), 0.0005, requests=(0.2, 0.5))
    assert max(abs(e) for e in errors) < 0.05, errors


def test_a_flat_top_is_reported():
    x = np.arange(0, 5 + 1e-9, 0.025)
    clipped = LaserFit.fit(x, np.clip(x / 3, None, 1))
    assert clipped.flat_top_volts() == pytest.approx(3.0, abs=0.1)
    assert LaserFit.fit(x, np.clip((x - 1.5) / 3.5, 0, None)).flat_top_volts() is None


def test_a_formula_still_fits_a_smooth_laser():
    x = np.arange(0, 5 + 1e-9, 0.025)
    y = (x / 5) ** 2 + np.random.default_rng(0).normal(0, 0.005, len(x))
    assert LaserFit.fit(x, y).fit_type == "poly3"


def test_linear_laser():
    cal = LaserCalibration([0, 5], [0, 50])
    assert cal.mw_to_control(25) == 2.5
    assert cal.mw_to_control(-3) == 0.0
    assert cal.control_to_mw(2.5) == 25
