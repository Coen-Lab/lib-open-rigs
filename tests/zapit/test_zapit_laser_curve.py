import numpy as np
import pytest

from open_rigs.zapit.laser import LaserFitRefused
from open_rigs.zapit import laser_curve as lc


def test_staircase_needs_the_test_subject():
    with pytest.raises(PermissionError):
        lc.staircase_volts([0, 5], test_subject=False)
    v = lc.staircase_volts([0, 5], 11, test_subject=True)
    assert v[0] == 0 and v[-1] == 5 and len(v) == 11


RNG = np.random.default_rng(0)   # shared across calls, as the default argument it replaces was


def photodiode(v, k=8, rng=RNG):
    level = 0.1 + 3 / (1 + np.exp(-1.6 * (v - 2.6)))
    s = np.repeat(level[:, None], k, axis=1) + rng.normal(0, 0.01, (len(v), k))
    s[:, : k // 2] -= 0.4    # the settling half reads low and must be ignored
    return s


def test_staircase_refuses_a_disabled_laser():
    with pytest.raises(PermissionError, match="ceilingMw 0"):
        lc.staircase_volts([0, 5], 11, test_subject=True, ceiling_mw=0)
    assert lc.staircase_volts([0, 5], 11, test_subject=True, ceiling_mw=5)[-1] == 5


def test_fit_from_a_staircase():
    v = lc.staircase_volts([0, 5], 51, test_subject=True)
    fit = lc.fit_laser_curve(v, photodiode(v))
    assert fit.problem() is None
    np.testing.assert_allclose(fit.evaluate(v), 0.1 + 3 / (1 + np.exp(-1.6 * (v - 2.6))), atol=0.05)


def test_flat_samples_are_accepted_in_step_order():
    v = lc.staircase_volts([0, 5], 51, test_subject=True)
    fit = lc.fit_laser_curve(v, photodiode(v).ravel())
    assert fit.problem() is None


def test_a_photodiode_out_of_the_beam_is_refused():
    v = lc.staircase_volts([0, 5], 51, test_subject=True)
    flat = np.random.default_rng(1).normal(0.02, 0.01, (51, 8))
    with pytest.raises(LaserFitRefused):
        lc.fit_laser_curve(v, flat)


def test_wrong_sample_count():
    with pytest.raises(ValueError):
        lc.step_means(np.zeros(10), 3)
