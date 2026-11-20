"""A task whose trials are drawn from weighted trial conditions, once for every such task.

A task's parameters model takes `ConditionedTask` beside its lab blocks and states its own RULES, TRIAL_TYPES, REQUIRED_CONDITIONS and
SESSION_FIELDS (COMMON_SESSION_FIELDS and its own) as class attributes; the mixin then validates its trial conditions as the
C# draw (OpenRigs.Trials TrialConditions) builds them, every combination checked as a task, and calls the task's
`between_trials` hook on the weighted combinations. `schema_extra` gives the model_config's x- keys the experiment launcher reads.
`condition_combinations` is the Python copy of the C# build; `expected_combination_rows` turns it into Settings_Combinations'
rows for the agreement test each task runs on its golden files (test_combinations).
"""

import copy
import json
import math
import subprocess
from functools import cache
from pathlib import Path
from typing import Annotated, Any, ClassVar, Literal, cast

import pydantic
from pydantic import (
    ConfigDict,
    Field,
    ValidationError,
    ValidationInfo,
    WithJsonSchema,
    field_validator,
    model_validator,
)

from open_rigs.core.base import Double, String
from open_rigs.core.task import STIMULUS_LEVEL_DBFS, SoundLevelDbfs, TaskBlock, level_dbfs
from open_rigs.rigs import conditions

ALL_SPEAKERS = "all"  # an auditoryStimulus.initialAzimuthsDeg item: every rig.yml speaker plays (OpenRigs.Audio SoundAzimuths.AllSpeakers)
# A sound azimuth or "all". oneOf, not pydantic's anyOf: Bonsai.Sgen makes an empty class of an anyOf item, object of a oneOf.
SoundAzimuth = Annotated[Double | Literal["all"], WithJsonSchema({"oneOf": [{"type": "number"}, {"const": ALL_SPEAKERS, "type": "string"}]})]
SIDELESS = [0, ALL_SPEAKERS]  # sound azimuths with no side
MAX_WEIGHT = 1_000_000  # the most one condition's weight may be (OpenRigs.Trials TrialConditions.MaxWeight)
MAX_WEIGHT_SUM = 2147483647  # the most the weights may add to: the draw sums them as an int (int.MaxValue)

# The session fields every conditioned task has (x-session-fields; a task adds its own): only what really cannot change per
# trial, so a condition may not set it (the path or any path under it). Everything else is condition-level, logged per trial
# through trialCombinationId and Settings_Combinations.
COMMON_SESSION_FIELDS = {
    "rngSeed": "seeds the one random stream every trial is drawn from, at Go",
    "curriculum": "the stage these values are: one per task file",
    "trialConditions": "the mix the conditions are",
    "optogenetics.enabled": "Zapit is connected and its conditions file loaded at Go",
    "optogenetics.zapitConditionsPath": "the conditions file loaded in Zapit and counted at Go",
}
# One block order for every conditioned task's model (top-level fields by alias): identity, the mix, timing, stimuli,
# reward, penalty, optogenetics, then the task's own (block_order_problems).
BLOCK_ORDER = ("rngSeed", "curriculum", "trialConditions", "trialStructure", "visualStimulus", "auditoryStimulus",
               "reward", "autoRewardProbability", "penalty", "optogenetics")


def props(schemas: dict) -> dict:
    return {"properties": schemas}


def if_(schemas: dict) -> dict:
    """An if over these paths, required: JSON Schema passes a missing path's properties vacuously."""
    return {"properties": schemas, "required": list(schemas)}


ABOVE_0 = {"contains": {"exclusiveMinimum": 0}}
SOUNDING = {"contains": {"type": "number"}}  # a level list holding a number: a sound plays (mute is the only text)


def schema_extra(session_fields: dict, rules, trial_types, required_conditions=()) -> dict:
    """A conditioned task's model_config json_schema_extra: x-session-fields, x-rules, x-trial-types, x-required-conditions, x-max-combinations."""
    return {"x-session-fields": list(session_fields), "x-rules": list(rules), "x-trial-types": list(trial_types),
            "x-required-conditions": list(required_conditions), "x-max-combinations": conditions.MAX_COMBINATIONS}


def get_path(data: dict, dotted: str) -> Any:
    for part in dotted.split("."):
        data = data[part]
    return data


def put_path(data: dict, dotted: str, value: Any) -> None:
    *parents, key = dotted.split(".")
    for part in parents:
        data = data[part]
    if key not in data:
        raise KeyError(dotted)
    data[key] = value


def _options(base: dict, values: dict[str, Any], path: str, list_fields) -> list:
    # A cell's picks: a list field's entries, each a one-entry list; a scalar field's list, else its one value (a whole
    # list, x-whole, is one value).
    value = values[path] if path in values else get_path(base, path)
    if path in list_fields:
        return [[entry] for entry in value] if isinstance(value, list) else [value]
    return value if isinstance(value, list) and not isinstance(get_path(base, path), list) else [value]


def cells(base: dict, values: dict[str, Any], pairs, list_fields) -> list[tuple[tuple[str, ...], list[tuple]]]:
    """A condition's cells (each pairs group, then each other path), each with its picks; none empty."""
    found: list[tuple[tuple[str, ...], list[tuple]]] = []
    paired = [path for group in pairs for path in group]
    for group in pairs:
        lists = [_options(base, values, path, list_fields) for path in group]
        if len({len(options) for options in lists}) > 1:
            raise ValueError(f"pairs {group} have lists of lengths {', '.join(str(len(options)) for options in lists)}; paired lists must be equally long")
        found.append((tuple(group), list(zip(*lists))))
    found += [((path,), [(option,) for option in _options(base, values, path, list_fields)]) for path in sorted({*values, *list_fields} - {*paired})]
    for paths, options in found:
        if not options:
            raise ValueError(f"{', '.join(paths)} is an empty list")
    return found


def product(found) -> list[dict[str, Any]]:
    combos: list[dict[str, Any]] = [{}]
    for paths, options in found:
        combos = [{**combo, **dict(zip(paths, option))} for option in options for combo in combos]
    return combos


def condition_combinations(base: dict, values: dict[str, Any], pairs, list_fields) -> list[dict[str, Any]]:
    """Every combination of a condition: each pairs group's lists zipped (equal lengths), then by path each other list cell
    of its set and every list field (the set's or the task's list) one pick, all combined as a product, the first cell's
    pick changing fastest (TrialConditions.Build's order: Settings_Combinations' rows, conditions in turn). A list field's
    entry is a one-entry list. The C# build is the rule the rig runs; this copy is for the task model's validation and the
    agreement test (expected_combination_rows)."""
    return product(cells(base, values, pairs, list_fields))


def session_field(path: str, session_fields) -> str | None:
    """The session field a dotted path is or lies under, else None."""
    return next((field for field in session_fields if path == field or path.startswith(field + ".")), None)


class TrialCondition(TaskBlock):
    """One kind of trial in the mix: its relative weight, its condition-level values (set) by dotted path and the lists it
    pairs. Its trialType is not stored: it is derived from its values (x-trial-types, derive_trial_type; the C#'s
    TrialTypes.Derive at Go) and logged as each trial's trialType."""

    model_config = ConfigDict(extra="forbid")

    weight: int = Field(ge=0, le=MAX_WEIGHT, description="Relative draw weight: each new trial picks a condition with these as relative chances")
    pairs: list[list[String]] = Field(
        default_factory=list,
        description="Groups of paths whose lists (the set's, else the task's) have equal length and share one picked index per trial. Unpaired lists are picked independently",
    )
    set: dict[str, Any] = Field(default_factory=dict, description="Condition-level values by dotted path, any field but x-session-fields'; the rest are the task's")


class Curriculum(TaskBlock):
    """The Mouse Curriculum stage these values are, and the task file's only record of the stage: the launcher fills it at
    launch from the share's curriculum file, its stage's values written over the task file's; empty in a repo's task files.
    Bonsai only records it (OpenRigs.Core CurriculumCheck.Problem, the same rules)."""

    id: int = Field(ge=0, description="The stage's id in the curriculum, fixed and hidden: never shown to the experimenter")
    code: String = Field(pattern=r"^S\d{2}[A-Z]$", description="The stage's code in the curriculum, S then two digits then a capital letter (S03A)")
    version: String = Field(pattern=r"^[abcdefghjkmnpqrstuvwxyz23456789]{4}$", description="The stage's version, 4 characters from abcdefghjkmnpqrstuvwxyz23456789 (k7f2)")
    path: String = Field(pattern=r"^[A-Z]$", description="The stage's path in the curriculum, one capital letter (A)")
    label: String = Field(description="The stage's name in the curriculum")
    sha256: String = Field(description="SHA-256 of the curriculum file's text the stage was read from")


class AuditoryStimulus(TaskBlock):
    """A sound stimulus: its level (mute for none), the azimuths it plays at (a number, or "all" for every rig.yml speaker)
    and the elevation the nearest speaker is found at. A task subclasses it to give its own defaults and descriptions."""

    level_dbfs: SoundLevelDbfs = level_dbfs(STIMULUS_LEVEL_DBFS, "The stimulus sound")
    elevation_deg: Double = Field(default=0.0, ge=-90, le=90, description="Elevation the sound plays at, in degrees: with the azimuth drawn, it picks rig.yml's nearest speaker")
    initial_azimuths_deg: list[SoundAzimuth] = Field(
        default=[ALL_SPEAKERS], min_length=1, description="Signed azimuths the sound plays at, in degrees, one picked per trial, on rig.yml's speaker nearest it; \"all\" on every speaker"
    )

    @field_validator("initial_azimuths_deg")
    @classmethod
    def _in_range(cls, value: list) -> list:
        # OpenRigs.Audio SoundAzimuths' same rule.
        for azimuth in value:
            if azimuth != ALL_SPEAKERS and not -180 <= azimuth <= 180:
                raise ValueError(f"initial_azimuths_deg value {azimuth} is outside [-180, 180]")
        return value


@cache
def _list_fields(model: type) -> tuple[str, ...]:
    return tuple(conditions.pick_lists(model.model_json_schema(by_alias=True)))


class ConditionedTask(TaskBlock):
    """A mixin block for a task parameters model with trialConditions (never on Task; its fields are the task's): validates the conditions as the C# draw
    builds them. The model gives RULES, TRIAL_TYPES, REQUIRED_CONDITIONS and SESSION_FIELDS; between_trials is its hook for the timing
    rules between a trial and the next."""

    SESSION_FIELDS: ClassVar[dict[str, str]] = COMMON_SESSION_FIELDS
    RULES: ClassVar[tuple] = ()
    TRIAL_TYPES: ClassVar[tuple] = ()
    REQUIRED_CONDITIONS: ClassVar[tuple] = ()

    @classmethod
    def list_fields(cls) -> tuple[str, ...]:
        """The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists)."""
        return _list_fields(cast(type, cls))

    @classmethod
    def between_trials(cls, tasks: list[dict], context: dict | None = None) -> None:
        """The timing rules between a trial and the next, on every weighted combination's values (by alias, as JSON); raise
        ValueError on a break. None by default. `context`, the validation's (a Zapit file's ramp-down, RAMP_DOWN_CONTEXT;
        ramp_down_from reads it), is always passed, None without one."""

    def derive_trial_type(self, condition: TrialCondition) -> str:
        """A condition's trialType from its settings (its set over the task's), by TRIAL_TYPES (x-trial-types)."""
        return conditions.derive_trial_type(list(self.TRIAL_TYPES), self.model_dump(by_alias=True, mode="json"), condition.set, condition.pairs)  # type: ignore[attr-defined]

    @model_validator(mode="after")
    def _weights_and_rules(self, info: ValidationInfo):
        # x-rules on the task's own values (each combination's in _conditions_run), the weights' sum.
        problems = conditions.rule_problems(list(self.RULES), self.model_dump(by_alias=True, mode="json"))  # type: ignore[attr-defined]
        if problems:
            raise ValueError("; ".join(f"{problem} (x-rules)" for problem in problems))
        weights = sum(condition.weight for condition in self.trial_conditions)  # type: ignore[attr-defined]
        if not weights > 0:
            raise ValueError("trial_conditions needs a condition with a weight above 0")
        if weights > MAX_WEIGHT_SUM:
            raise ValueError(f"trial_conditions' weights add to more than {MAX_WEIGHT_SUM}, the most the draw can sum")
        return self

    @model_validator(mode="after")
    def _conditions_run(self, info: ValidationInfo):
        # The C# build: each condition's set and pairs over the task's values, every combination (at most
        # MAX_COMBINATIONS), each a task that must validate; then x-required-conditions and between_trials, on the weighted
        # combinations.
        if (info.context or {}).get("condition"):
            return self
        base = self.model_dump(by_alias=True, mode="json")  # type: ignore[attr-defined]
        leaves = conditions.flatten(base)
        optogenetics = getattr(self, "optogenetics", None)
        drawn, built = [], {}  # built: each distinct set and pairs' combinations, checked once
        for i, condition in enumerate(self.trial_conditions):  # type: ignore[attr-defined]
            try:
                where = f"trial_conditions[{i}] ({self.derive_trial_type(condition)})"
            except ValueError as e:
                raise ValueError(f"trial_conditions[{i}]: {e}") from None
            paired = [path for group in condition.pairs for path in group]
            for path in [*condition.set, *paired]:
                if field := session_field(path, self.SESSION_FIELDS):
                    raise ValueError(f"{where} sets or pairs {path}, a session field (x-session-fields: {field} {self.SESSION_FIELDS[field]})")
                if path not in leaves:
                    raise ValueError(f"{where} sets or pairs {path}, which is not a task field")
                if path.startswith("optogenetics.") and optogenetics is not None and not optogenetics.enabled:
                    raise ValueError(f"{where} sets {path} with optogenetics off")
            if len(set(paired)) < len(paired) or any(len(group) < 2 for group in condition.pairs):
                raise ValueError(f"{where}: each pairs group needs two paths or more, each path in one group")
            key = json.dumps([condition.set, condition.pairs], sort_keys=True)
            if key not in built:
                built[key] = self._combinations(base, condition, where, info.context or {})
            drawn += built[key] * (condition.weight > 0)
        if problems := conditions.required_problems(list(self.REQUIRED_CONDITIONS), base, drawn):
            raise ValueError("; ".join(f"{problem} (x-required-conditions)" for problem in problems))
        self.between_trials(drawn, context=info.context)
        return self

    def _combinations(self, base: dict, condition: TrialCondition, where: str, context: dict) -> list[dict]:
        """A condition's combinations as tasks' values (by alias, as JSON), each checked as a task."""
        try:
            found = cells(base, condition.set, condition.pairs, self.list_fields())
        except ValueError as e:
            raise ValueError(f"{where}: {e}") from None
        # Counted before any is built, as the C# does, so an oversized condition is refused at once.
        count = math.prod(len(options) for _, options in found)
        if count > conditions.MAX_COMBINATIONS:
            raise ValueError(f"{where} expands into {count} combinations, more than {conditions.MAX_COMBINATIONS} (x-max-combinations)")
        tasks = []
        for combo in product(found):
            data = copy.deepcopy(base)
            for path, value in combo.items():
                put_path(data, path, value)
            data["trialConditions"] = [{"weight": 1}]
            try:
                type(self).model_validate(data, context={**context, "condition": True})  # type: ignore[attr-defined]
            except ValidationError as e:
                raise ValueError(f"{where} with {combo}: {e}") from None
            tasks.append(data)
        return tasks


def block_order_problems(model: type[pydantic.BaseModel]) -> list[str]:
    """Where a conditioned task's top-level fields (by alias) leave BLOCK_ORDER: the BLOCK_ORDER fields it has must come in
    that order and before its own."""
    names = [field.alias or name for name, field in model.model_fields.items()]
    shared = [name for name in names if name in BLOCK_ORDER]
    problems = []
    if shared != sorted(shared, key=BLOCK_ORDER.index):
        problems.append(f"the lab's blocks are in the order {', '.join(shared)}, not {', '.join(sorted(shared, key=BLOCK_ORDER.index))}")
    own = [i for i, name in enumerate(names) if name not in BLOCK_ORDER]
    if own and shared and own[0] < names.index(shared[-1]):
        problems.append(f"{names[own[0]]} comes before {shared[-1]}: a task's own fields follow the lab's blocks")
    return problems


def _normal(path: str, value: Any, list_fields) -> Any:
    return value[0] if path in list_fields and isinstance(value, list) and len(value) == 1 else value


def expected_combination_rows(model: type[pydantic.BaseModel], task: dict) -> list[dict]:
    """Settings_Combinations' rows as the Python build makes them from a task's values (as the task file has them): every
    condition's combinations in turn, a list field's one-entry pick unwrapped (an x-whole list stays a list), a path kept
    where it differs between rows or from the task's own value. The agreement test holds the C#'s file to it."""
    list_fields = conditions.pick_lists(model.model_json_schema(by_alias=True))
    parsed = model.model_validate(task)
    base = parsed.model_dump(by_alias=True, mode="json")
    rows = []
    for i, condition in enumerate(task["trialConditions"]):
        trial_type = parsed.derive_trial_type(parsed.trial_conditions[i])  # type: ignore[attr-defined]
        for combo in condition_combinations(base, condition.get("set") or {}, condition.get("pairs") or [], list_fields):
            rows.append({"trialCombinationId": len(rows), "trialConditionId": i, "trialType": trial_type,
                         "conditionWeight": condition["weight"], "values": {path: _normal(path, value, list_fields) for path, value in combo.items()}})
    paths = {path for row in rows for path in row["values"]}
    own = {path: _normal(path, get_path(base, path), list_fields) for path in paths}

    def value(row: dict, path: str) -> Any:
        return row["values"].get(path, own[path])

    kept = {path for path in paths if any(value(row, path) != own[path] or value(row, path) != value(rows[0], path) for row in rows)}
    for row in rows:
        row["values"] = {path: value(row, path) for path in sorted(kept)}
    return rows


def combination_agreement_problems(model: type[pydantic.BaseModel], fixtures: Path) -> list[str]:
    """Each golden task file (fixtures/handover/*.yml) whose C# Settings_Combinations (fixtures/combinations/<stem>.json,
    written by the task's HandoverTests) differs from expected_combination_rows, or has none."""
    import yaml

    problems = []
    golden = sorted((fixtures / "handover").glob("*.yml"))
    if not golden:
        return [f"{fixtures / 'handover'} holds no golden task file"]
    for path in golden:
        written = fixtures / "combinations" / f"{path.stem}.json"
        if not written.is_file():
            problems.append(f"{path.stem}: no {written.name}")
            continue
        task = yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]
        if json.loads(written.read_text(encoding="utf-8"))["rows"] != expected_combination_rows(model, task):
            problems.append(f"{path.stem}: the C#'s Settings_Combinations differ from the Python build's")
    extra = {p.stem for p in (fixtures / "combinations").glob("*.json")} - {p.stem for p in golden}
    problems += [f"{stem}: a combinations file with no golden task file" for stem in sorted(extra)]
    return problems


def handover_problems(model: type[pydantic.BaseModel], fixtures: Path) -> list[str]:
    """Each golden task file (fixtures/handover/*.yml, written by the experiment launcher) the task model refuses (a stored
    trialType among them: it is derived, never stored)."""
    import yaml

    golden = sorted((fixtures / "handover").glob("*.yml"))
    if not golden:
        return [f"{fixtures / 'handover'} is empty: run the launcher's tests/test_handover.py with HANDOVER_REGEN=1"]
    problems = []
    for path in golden:
        try:
            model.model_validate(yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"])
        except ValidationError as e:
            problems.append(f"{path.name}: {e}")
    return problems


def task_schema_cs(session_fields, list_fields, trial_types) -> str:
    """TaskSchema.Generated.cs: what a task's C# reads from its schema rather than copying it."""
    session = ", ".join(f'"{path}"' for path in session_fields)
    lists = ", ".join(f'"{path}"' for path in list_fields)
    types = json.dumps(list(trial_types), separators=(",", ":")).replace('"', '""')
    return (
        "// Generated by regenerate-schemas from the task schema; do not edit.\n"
        "public static class TaskSchema\n{\n"
        "    /// <summary>x-session-fields: what a condition may not set (the path or any path under it).</summary>\n"
        f"    public static readonly string[] SessionFields = {{ {session} }};\n"
        "    /// <summary>x-max-combinations: the most combinations one condition may expand into.</summary>\n"
        f"    public const int MaxCombinations = {conditions.MAX_COMBINATIONS};\n"
        "    /// <summary>The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists), by path.</summary>\n"
        f"    public static readonly string[] ListFields = {{ {lists} }};\n"
        "    /// <summary>x-trial-types as JSON: how a condition's trialType is derived from its values (OpenRigs.Trials TrialTypes).</summary>\n"
        f'    public const string TrialTypes = @"{types}";\n'
        "}\n"
    )


EXTENSIONS_ROOT = Path("./src/Extensions/")


def regenerate(logic: type, parameters: type, schema_file: Path, extensions_root: Path = EXTENSIONS_ROOT) -> None:
    """A conditioned task's regenerate-schemas: TaskSchema.Generated.cs, the task logic's JSON schema and its Bonsai.Sgen C#."""
    schema_file.parent.mkdir(parents=True, exist_ok=True)
    (extensions_root / "TaskSchema.Generated.cs").write_text(task_schema_cs(parameters.SESSION_FIELDS, parameters.list_fields(), parameters.TRIAL_TYPES), encoding="utf-8")
    schema = pydantic.RootModel[logic].model_json_schema(by_alias=True, mode="serialization", union_format="primitive_type_array")
    schema_file.write_text(json.dumps(schema, indent=2))
    subprocess.run(["dotnet", "bonsai.sgen", str(schema_file), "--output", str(extensions_root), "--serializer", "json", "--serializer", "yaml"], check=True)
