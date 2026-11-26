"""The laser curve from a staircase: the volts the workflow steps through and the fit of the photodiode's reading.

The staircase is the one place the ceiling does not apply, since the curve must cover the laser's physical range with
the photodiode in the beam; it is handed out only for the lab's test subject, and refused without ``test_subject``.
"""
from __future__ import annotations

import numpy as np

from open_rigs.zapit.laser import SWEEP_POINTS, LaserFit, LaserFitRefused
from open_rigs.zapit.safety import laser_disabled

SETTLE_FRACTION = 0.5   # the first half of each step's photodiode samples is the photodiode settling


def staircase_volts(min_max_control_volts, points: int = SWEEP_POINTS, *, test_subject: bool,
                    ceiling_mw: float | None = None) -> np.ndarray:
    """Control volts for ``points`` even steps over the laser's range. ``ceilingMw`` does not cap it, so it is
    refused unless the subject is the lab's ``test`` mouse (``test_subject=True``). A ``ceiling_mw`` of 0 (laser
    disabled) refuses it too."""
    if ceiling_mw is not None and laser_disabled(ceiling_mw):
        raise PermissionError("The laser is disabled (ceilingMw 0): the laser-curve staircase will not run")
    if not test_subject:
        raise PermissionError("The laser-curve staircase ignores the power ceiling and runs only with the test subject")
    lo, hi = (float(v) for v in min_max_control_volts)
    if not hi > lo or points < 4:
        raise ValueError("The staircase needs a rising voltage range and at least 4 steps")
    return np.linspace(lo, hi, int(points))


def step_means(samples, n_steps: int, settle_fraction: float = SETTLE_FRACTION) -> np.ndarray:
    """One photodiode reading per step. ``samples`` is (n_steps, k), or flat with k per step in step order; the
    first ``settle_fraction`` of each step's samples is dropped."""
    a = np.asarray(samples, dtype=float)
    if a.ndim == 1:
        if a.size % n_steps:
            raise ValueError(f"{a.size} photodiode samples do not divide into {n_steps} steps")
        a = a.reshape(n_steps, -1)
    if a.ndim != 2 or a.shape[0] != n_steps:
        raise ValueError(f"Expected photodiode samples for {n_steps} steps, got shape {a.shape}")
    return a[:, int(a.shape[1] * settle_fraction):].mean(axis=1)


def fit_laser_curve(volts, samples, settle_fraction: float = SETTLE_FRACTION) -> LaserFit:
    """Fit the staircase. Raises ``LaserFitRefused`` (carrying the fit) when the reading does not rise steadily
    with the voltage, as with a photodiode out of the beam."""
    volts = np.asarray(volts, dtype=float)
    fit = LaserFit.fit(volts, step_means(samples, len(volts), settle_fraction))
    problem = fit.problem()
    if problem:
        raise LaserFitRefused(f"{problem}. Is the photodiode in the beam?", fit)
    return fit
