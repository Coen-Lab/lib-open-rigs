"""The conditioned-task mixin and helpers (open_rigs.rigs.conditioned_task) on a small task built here."""

import json

import pytest
import yaml
from pydantic import Field, ValidationError

from open_rigs.core.task import Optogenetics, Reward, TaskParameters
from open_rigs.rigs import conditioned_task as ct


class TrialCondition(ct.TrialCondition):
    """A condition of the test task."""


class Opto(Optogenetics):
    laser_on_probability: float = 0.0
    laser_to_stimulus_seconds: float = 0.0
    trials_before_laser: int = 0


class Params(TaskParameters, ct.ConditionedTask):
    SESSION_FIELDS = {**ct.COMMON_SESSION_FIELDS, "taskType": "fixed"}
    RULES = ({"if": ct.if_({"contrasts": {"items": {"const": 0}}}), "unused": ["azimuthsDeg"], "message": "no grating"},)
    TRIAL_TYPES = ({"trialType": "visual", "if": ct.if_({"contrasts": ct.ABOVE_0})}, {"trialType": "neutral"})
    REQUIRED_CONDITIONS = ({"if": ct.if_({"taskType": {"const": "go"}}), "some": ct.if_({"contrasts": {"contains": {"const": 1}}}), "message": "go needs contrast 1"},)

    curriculum: ct.Curriculum | None = None
    trial_conditions: list[TrialCondition] = Field(min_length=1, json_schema_extra={"x-whole": True})
    contrasts: list[float] = [1.0]
    azimuths_deg: list[float] = [-60.0, 60.0]
    auditory_stimulus: ct.AuditoryStimulus = ct.AuditoryStimulus()
    reward: Reward = Reward(size_ul=2)
    optogenetics: Opto = Opto()
    task_type: str = "go"
    delay_seconds: float = 0.0

    @classmethod
    def between_trials(cls, tasks):
        if any(task["delaySeconds"] > 5 for task in tasks):
            raise ValueError("delay too long")


def task(**conditions):
    return {"trialConditions": [{"weight": 1, **conditions}]}


def test_list_fields_are_the_task_s_drawn_lists():
    assert Params.list_fields() == ("auditoryStimulus.initialAzimuthsDeg", "azimuthsDeg", "contrasts")


def test_a_valid_task_derives_its_trial_types():
    params = Params.model_validate(task(set={"contrasts": [0.5, 1.0]}))
    assert params.derive_trial_type(params.trial_conditions[0]) == "visual"


def test_a_stored_trial_type_is_an_unknown_key():
    with pytest.raises(ValidationError, match="trialType"):
        Params.model_validate(task(trialType="visual"))


def test_a_condition_no_trial_type_matches_is_refused_naming_it():
    class Untyped(Params):
        TRIAL_TYPES = ({"trialType": "visual", "if": ct.if_({"contrasts": ct.ABOVE_0})}, {"trialType": "x", "mirrored": ["contrasts", "azimuthsDeg"]})

    with pytest.raises(ValidationError, match=r"trial_conditions\[0\]: no x-trial-types entry matches"):
        Untyped.model_validate(task(set={"contrasts": [0.0]}))


@pytest.mark.parametrize("condition, message", [
    ({"set": {"taskType": "x"}}, "a session field (x-session-fields: taskType fixed)"),
    ({"set": {"rngSeed": 1}}, "a session field (x-session-fields: rngSeed"),
    ({"set": {"nope": 1}}, "which is not a task field"),
    ({"set": {"optogenetics.laserOnProbability": 1}}, "with optogenetics off"),
    ({"pairs": [["contrasts"]]}, "each pairs group needs two paths or more"),
    ({"pairs": [["contrasts", "azimuthsDeg"]], "set": {"contrasts": [1.0, 0.5, 0.2]}}, "paired lists must be equally long"),
    ({"set": {"contrasts": [2.0, 1.0]}, "weight": 0}, "needs a condition with a weight above 0"),
    ({"set": {"contrasts": [0.0]}}, "go needs contrast 1 (x-required-conditions)"),
    ({"set": {"delaySeconds": 9}}, "delay too long"),
])
def test_a_condition_is_refused_naming_why(condition, message):
    with pytest.raises(ValidationError, match=message.replace("(", r"\(").replace(")", r"\)")):
        Params.model_validate(task(**condition))


def test_combinations_pair_and_multiply_first_cell_fastest():
    base = Params.model_validate(task()).model_dump(by_alias=True, mode="json")
    combos = ct.condition_combinations(base, {"delaySeconds": [1, 2]}, [], Params.list_fields())
    assert [(c["delaySeconds"], c["azimuthsDeg"]) for c in combos] == [(1, [-60.0]), (1, [60.0]), (2, [-60.0]), (2, [60.0])]
    paired = ct.condition_combinations(base, {"contrasts": [0.5, 1.0]}, [["contrasts", "azimuthsDeg"]], Params.list_fields())
    assert [(c["contrasts"], c["azimuthsDeg"]) for c in paired] == [([0.5], [-60.0]), ([1.0], [60.0])]


def test_expected_rows_keep_only_paths_that_differ(tmp_path):
    values = task(set={"contrasts": [0.5, 1.0]})
    rows = ct.expected_combination_rows(Params, values)
    assert [row["values"] for row in rows] == [{"azimuthsDeg": a, "contrasts": c} for c in (0.5, 1.0) for a in (-60.0, 60.0)]
    assert rows[0]["trialConditionId"] == 0 and [row["trialCombinationId"] for row in rows] == [0, 1, 2, 3]
    assert {row["trialType"] for row in rows} == {"visual"}
    (tmp_path / "handover").mkdir()
    (tmp_path / "combinations").mkdir()
    (tmp_path / "handover" / "a.yml").write_text(yaml.safe_dump({"taskParameters": values}), encoding="utf-8")
    assert ct.combination_agreement_problems(Params, tmp_path) == ["a: no a.json"]
    (tmp_path / "combinations" / "a.json").write_text(json.dumps({"rows": rows}), encoding="utf-8")
    assert ct.combination_agreement_problems(Params, tmp_path) == []
    assert ct.handover_problems(Params, tmp_path) == []
    stored = {"trialConditions": [{"trialType": "visual", "weight": 1}]}
    (tmp_path / "handover" / "a.yml").write_text(yaml.safe_dump({"taskParameters": stored}), encoding="utf-8")
    assert "trialType" in ct.handover_problems(Params, tmp_path)[0]


def test_block_order_problems():
    assert ct.block_order_problems(Params) == ["contrasts comes before optogenetics: a task's own fields follow the lab's blocks"]

    class Disordered(TaskParameters):
        reward: Reward
        curriculum: ct.Curriculum | None = None

    assert ct.block_order_problems(Disordered) == [
        "the lab's blocks are in the order rngSeed, reward, curriculum, not rngSeed, curriculum, reward"]

    class Ordered(TaskParameters):
        curriculum: ct.Curriculum | None = None
        reward: Reward
        own_field: int = 0

    assert ct.block_order_problems(Ordered) == []


def test_schema_extra_and_task_schema_cs():
    extra = ct.schema_extra(Params.SESSION_FIELDS, Params.RULES, Params.TRIAL_TYPES)
    assert extra["x-session-fields"][:3] == ["rngSeed", "curriculum", "trialConditions"] and extra["x-max-combinations"] == 1024
    text = ct.task_schema_cs(["rngSeed"], ["a", "b"], Params.TRIAL_TYPES)
    assert 'ListFields = { "a", "b" }' in text
    assert 'TrialTypes = @"[{""trialType"":""visual"",""if"":{""properties"":{""contrasts"":{""contains"":{""exclusiveMinimum"":0}}},""required"":[""contrasts""]}},{""trialType"":""neutral""}]";' in text


def test_auditory_azimuths_are_numbers_in_range_or_all():
    assert ct.AuditoryStimulus(initial_azimuths_deg=["all", -60]).initial_azimuths_deg == ["all", -60]
    with pytest.raises(ValidationError, match="outside"):
        ct.AuditoryStimulus(initial_azimuths_deg=[200])


def test_a_condition_sets_a_penalised_miss_s_sound_level_by_its_three_level_path():
    from open_rigs.core.task import Penalty
    from open_rigs.rigs import conditions

    class Penalised(Params):
        penalty: Penalty = Penalty(timeout_seconds=2)

    path = "penalty.noResponse.sound.levelDbfs"
    assert conditions.flatten(Penalised.model_validate(task()).model_dump(by_alias=True, mode="json"))[path] is None
    rows = ct.expected_combination_rows(Penalised, task(set={path: ["mute", -40]}))
    assert [row["values"][path] for row in rows] == ["mute", "mute", -40, -40]  # azimuthsDeg picks fastest
    with pytest.raises(ValidationError, match="levelDbfs"):
        Penalised.model_validate(task(set={path: "off"}))
