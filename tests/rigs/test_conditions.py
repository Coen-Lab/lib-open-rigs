import pytest

from open_rigs.rigs.conditions import condition_combinations, derive_trial_type, flatten, pick_lists, required_problems, rule_problems, unused_paths

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
    ({V: [60, "all"], A: [-60, "all"]}, [[V, A]], "coherent"),  # a non-number is never mirrored
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


def test_unused_paths_are_the_applying_rules():
    assert unused_paths(RULES, {**BASE, "contrasts": [0]}) == {V}
    assert unused_paths(RULES, BASE) == set()


@pytest.mark.parametrize("rule", [
    {"if": {}, "then": {}},
    {"if": {}, "then": {}, "unused": [], "message": "m"},
    {"if": {}, "then": {}, "message": "m", "unless": {}},
])
def test_a_malformed_x_rule_fails_loudly(rule):
    with pytest.raises(ValueError, match="malformed"):
        rule_problems([rule], BASE)


def test_an_if_naming_a_path_not_in_the_values_fails_loudly():
    rule = {"if": {"properties": {"targt": {"const": "side"}}}, "unused": [V], "message": "m"}
    with pytest.raises(ValueError, match="x-rules entry .* names targt, not in the values"):
        unused_paths([rule], BASE)
    entry = {"trialType": "x", "if": {"properties": {"contrast": SOME_ABOVE_0}}}
    with pytest.raises(ValueError, match="x-trial-types entry .* names contrast, not in the values"):
        derive_trial_type([entry, {"trialType": "y"}], BASE, {})


GO_NO_GO = [{"if": {"properties": {"taskType": {"const": "goNoGo"}}, "required": ["taskType"]},
             "some": {"properties": {"target": {"const": "go"}}, "required": ["target"]}, "message": "needs a go condition"}]


@pytest.mark.parametrize("task_type, targets, problems", [
    ("goNoGo", ["noGo", "go"], []),
    ("goNoGo", ["noGo"], ["needs a go condition"]),
    ("goNoGo", [], ["needs a go condition"]),
    ("other", ["noGo"], []),
])
def test_required_problems_need_some_combination_matching(task_type, targets, problems):
    task = {**BASE, "taskType": task_type}
    assert required_problems(GO_NO_GO, task, [{**task, "target": target} for target in targets]) == problems


@pytest.mark.parametrize("entry, words", [
    ({**GO_NO_GO[0], "description": "d"}, "malformed"),
    ({"if": {}, "message": "m"}, "malformed"),
    ({**GO_NO_GO[0], "some": {"required": ["targt"]}}, "names targt, not in the values"),
])
def test_a_malformed_x_required_conditions_entry_fails_loudly(entry, words):
    task = {**BASE, "taskType": "goNoGo"}
    with pytest.raises(ValueError, match=words):
        required_problems([entry], task, [task])


def test_pick_lists_are_every_array_but_a_whole_one():
    schema = {"properties": {"contrasts": {"type": "array"}, "trialConditions": {"type": "array", "x-whole": True}, "seed": {"type": "number"},
                             "sound": {"$ref": "#/$defs/Sound"}, "maybe": {"anyOf": [{"type": "array"}, {"type": "null"}]}},
              "$defs": {"Sound": {"properties": {"azimuths": {"type": ["array", "null"]}, "stimuli": {"type": "array", "x-whole": True}}}}}
    assert pick_lists(schema) == ["contrasts", "maybe", "sound.azimuths"]


def test_combinations_change_the_first_cell_fastest_and_zip_pairs():
    base = {"c": [1, 2], "a": "x", "p": [5, 6], "q": [7, 8]}
    got = condition_combinations(base, {"a": ["u", "v"], "p": [5, 6], "q": [7, 8]}, [["p", "q"]], ["c"])
    # cells in order: the pair, then a, then c; the pair's pick changes fastest.
    assert [(g["p"], g["q"], g["a"], g["c"]) for g in got][:5] == [(5, 7, "u", [1]), (6, 8, "u", [1]), (5, 7, "v", [1]), (6, 8, "v", [1]), (5, 7, "u", [2])]
    assert len(got) == 8
