"""The scanner fit chooses similarity, affine or polynomial by leave-one-out pointing error, as the lab's MATLAB Zapit
fix (zapit.utils.fitScannerTransform) does. On the rig (2026-10-27) a similarity fit left a 520 um mean residual and
the calibration was refused; the galvos have unequal gains, which only an affine fit follows."""
import numpy as np

from open_rigs.zapit.transforms import Affine, Polynomial, Similarity, select_scanner_transform

RNG = np.random.default_rng(1)


def grid(half=7.0, step=1.5):
    g = np.arange(-half, half + 1e-9, step)
    xx, yy = np.meshgrid(g, g)
    return np.c_[xx.ravel(), yy.ravel()]


def unequal_gains():
    """Gains 14% apart on the two axes with a slight shear: a similarity fit misses by hundreds of microns, as on the
    rig."""
    return Affine(np.array([[1.08, 0.02, 0.2], [-0.01, 0.94, -0.1], [0, 0, 1]]))


def test_a_similarity_is_kept_when_nothing_more_is_needed():
    target = grid()
    true = Similarity(np.array([[0.98, -0.05, 0.3], [0.05, 0.98, -0.2], [0, 0, 1]]))
    actual = true.forward(target) + RNG.normal(0, 0.01, target.shape)
    tf, info = select_scanner_transform(target, actual)
    assert info.chosen == "similarity" and isinstance(tf, Similarity)


def test_unequal_galvo_gains_choose_the_affine():
    target = grid()
    actual = unequal_gains().forward(target) + RNG.normal(0, 0.01, target.shape)
    sim_res = Similarity.fit(target, actual).residuals(target, actual).mean() * 1e3
    _, info = select_scanner_transform(target, actual)
    assert info.chosen == "affine"
    assert info.residuals_um.mean() < 25 < sim_res
    assert info.loo_rms_um["similarity"] > 3 * info.loo_rms_um["affine"]


def test_a_spot_far_off_the_rest_is_ignored():
    target = grid()
    actual = unequal_gains().forward(target) + RNG.normal(0, 0.01, target.shape)
    actual[5] += [0.8, -0.6]   # a glint taken for the beam
    _, info = select_scanner_transform(target, actual)
    assert info.n_outliers == 1 and info.residuals_um.max() < 60


def test_lens_distortion_chooses_a_polynomial():
    target = grid()
    r2 = (target ** 2).sum(axis=1, keepdims=True)
    actual = target * (1 + 0.002 * r2) + RNG.normal(0, 0.005, target.shape)   # pincushion, ~0.7 mm at the corners
    tf, info = select_scanner_transform(target, actual)
    assert info.chosen.startswith("polynomial") and isinstance(tf, Polynomial)
    assert np.linalg.norm(tf.inverse(actual) - target, axis=1).mean() < 0.05


def test_a_polynomial_that_would_extrapolate_is_refused():
    # Points over one corner only: a polynomial fitted there wanders far off elsewhere in the image.
    target = grid(half=2.0, step=1.0) + 4.0
    actual = target * (1 + 0.002 * (target ** 2).sum(axis=1, keepdims=True)) + RNG.normal(0, 0.005, target.shape)
    _, info = select_scanner_transform(target, actual, field_of_view=(-8, 8, -8, 8))
    assert not info.chosen.startswith("polynomial")
    assert info.refused


