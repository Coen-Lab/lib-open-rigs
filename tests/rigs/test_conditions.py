import pytest

from open_rigs.core.labels import derive_label

V, A = "visual.azimuths", "sound.azimuths"
LABELS = [
    {"label": "go", "when": "target", "is": "go"},
    {"label": "conflict", "anyAbove": {"contrasts": 0, "sound.amplitude": 0}, "anyNotIn": {A: [0, "all"]}, "mirrored": [V, A]},
    {"label": "coherent", "anyAbove": {"contrasts": 0, "sound.amplitude": 0}, "anyNotIn": {A: [0, "all"]}},
    {"label": "visual", "anyAbove": {"contrasts": 0}},
    {"label": "neutral"},
]
BASE = {"target": "side", "contrasts": [1.0], "visual": {"azimuths": [-60, 60]}, "sound": {"amplitude": 1.0, "azimuths": [-60, 60]}}


@pytest.mark.parametrize("values, pairs, label", [
    ({"target": "go"}, [], "go"),
    ({"target": ["go", "go"]}, [], "go"),
    ({"target": ["go", "side"]}, [], "coherent"),
    ({}, [], "coherent"),
    ({}, [[V, A]], "coherent"),
    ({A: [60, -60]}, [[V, A]], "conflict"),
    ({A: [60, -60]}, [], "coherent"),
    ({A: [0, "all"]}, [], "visual"),
    ({A: [0, 60]}, [], "coherent"),
    ({"sound.amplitude": [0, 0.5]}, [], "coherent"),
    ({"contrasts": [0], "sound.amplitude": 0}, [], "neutral"),
])
def test_the_first_matching_entry_names_the_condition(values, pairs, label):
    assert derive_label(LABELS, BASE, values, pairs) == label


@pytest.mark.parametrize("labels, words", [
    ([{"label": "x", "below": {"contrasts": 1}}], "malformed"),
    ([{"label": "x", "when": "target"}], "malformed"),
    ([{"label": "x", "when": "target", "is": "go"}], "no x-labels entry matches"),
])
def test_a_malformed_or_unmatched_x_labels_fails_loudly(labels, words):
    with pytest.raises(ValueError, match=words):
        derive_label(labels, BASE, {})
