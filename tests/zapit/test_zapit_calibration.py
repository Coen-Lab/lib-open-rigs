"""Calibration grid and spot finding."""
import numpy as np

from open_rigs.zapit.calibration import calibration_grid, find_beam_spot


def test_grid_is_centred():
    g = calibration_grid((1000, 600), 0.01, spacing_mm=2.0, buffer_mm=1.0)
    assert g.shape[1] == 2
    np.testing.assert_allclose(g.mean(axis=0), [0, 0], atol=1e-9)
    assert np.all(np.abs(g[:, 0]) <= 5 - 1 + 1e-9)
    assert np.all(np.abs(g[:, 1]) <= 3 - 1 + 1e-9)


def test_find_beam_spot_rejects_multiple_blobs():
    f = np.zeros((100, 100))
    f[20, 20] = 255
    f[80, 80] = 255
    assert find_beam_spot(f, None, 500) is None
    f[80, 80] = 0
    found = find_beam_spot(f, None, 500)
    assert found is not None
    c, area, _ = found
    np.testing.assert_allclose(c, [20, 20])
    assert area == 1


