import pytest

from open_rigs.rigs.conditions import derive_trial_type, flatten, rule_problems

V, A = "visual.azimuths", "sound.azimuths"
SOME_ABOVE_0 = {"contains": {"exclusiveMinimum": 0}}
SHOWN = {"properties": {"contrasts": SOME_ABOVE_0, "sound.amplitude": SOME_ABOVE_0, A: {"contains": {"not": {"enum": [0, "all"]}}}}}
TRIAL_TYPES = [
    {"trialType": "go", "if": {"properties": {"target": {"items": {"const": "go"}}}}},
    {"trialType": "conflict", "if": SHOWN, "mirrored": [V, A]},
    {"trialType": "coherent", "if": SHOWN},
    {"trialType": "visual", "if": {"properties": {"contrasts": SOME_ABOVE_0}}},
    {"trialType": "neutral"},
]
BASE = {"target": "side", "contrasts": [1.0], "visual": {"azimuths": [-60, 60]}, "sound": {"amplitude": 1.0, "azimuths": [-60, 60]}}
RULES = [
    {"if": {"properties": {"target": {"const": "side"}}}, "then": {"properties": {A: {"items": {"not": {"enum": [0, "all"]}}}}},
     "message": "target side needs sound azimuths without 0 or all"},
    {"if": {"properties": {"contrasts": {"items": {"const": 0}}}}, "unused": [V], "message": "contrast 0 shows no grating"},
]


def test_flatten_keys_leaves_by_dotted_path():
    assert flatten(BASE) == {"target": "side", "contrasts": [1.0], V: [-60, 60], "sound.amplitude": 1.0, A: [-60, 60]}


@pytest.mark.parametrize("values, pairs, trial_type", [
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
def test_the_first_matching_entry_names_the_condition(values, pairs, trial_type):
    assert derive_trial_type(TRIAL_TYPES, BASE, values, pairs) == trial_type


@pytest.mark.parametrize("trial_types, words", [
    ([{"trialType": "x", "if": {}, "below": 1}, {"trialType": "y"}], "malformed"),
    ([{"trialType": "x"}, {"trialType": "y"}], "malformed"),
    ([{"trialType": "x", "if": {}}], "malformed"),
    ([{"trialType": "x", "mirrored": [V, A]}], "no x-trial-types entry matches"),
])
def test_a_malformed_or_unmatched_x_trial_types_fails_loudly(trial_types, words):
    with pytest.raises(ValueError, match=words):
        derive_trial_type(trial_types, BASE, {})


@pytest.mark.parametrize("values, problems", [
    (BASE, []),
    ({**BASE, "sound": {"amplitude": 1.0, "azimuths": [0]}}, ["target side needs sound azimuths without 0 or all"]),
    ({**BASE, "target": "go", "sound": {"amplitude": 1.0, "azimuths": ["all"]}}, []),
])
def test_rule_problems_are_the_broken_rules_messages(values, problems):
    assert rule_problems(RULES, values) == problems


@pytest.mark.parametrize("rule", [
    {"if": {}, "then": {}},
    {"if": {}, "then": {}, "unused": [], "message": "m"},
    {"if": {}, "then": {}, "message": "m", "unless": {}},
])
def test_a_malformed_x_rule_fails_loudly(rule):
    with pytest.raises(ValueError, match="malformed"):
        rule_problems([rule], BASE)
