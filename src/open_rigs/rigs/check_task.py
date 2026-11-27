"""A task's own Python model run on its task values, for the experiment launcher to refuse a value as it
is typed rather than at Go: every rule the model holds (the timing between fields, each combination, between trials)
said once, in the task's Python, never copied into its schema.

A task names its parameters model in its pyproject.toml:

    [project.entry-points."open_rigs.task"]
    parameters = "my_task.task:MyTaskParameters"

`python -m open_rigs.rigs.check_task <task file>` prints the file's problems (exit 1 on any). With no file it serves:
its first line says {"model": ...} (or {"error": ...} and it exits), then each stdin line {"taskParameters": {...}} gets
one stdout line {"problems": [...]}, [] when the values fit (a line may add "zapitBank", a ZapitDaq session's
Settings_ZapitBank_<DateTime>.json once built, for its longest ramp-down), so one process answers every check (importing a model
takes seconds, a check milliseconds). When the values fit, Zapit-Bonsai is ticked and the model gives its trigger_gap_seconds,
the answer adds "zapitTrialSeconds" (TRIAL_SECONDS): ZapitDaq's trial buffer from the task's timing (zapit_trial_seconds), which
the launcher checks Go against and hands the workflow in place of rig.yml's retired maxTrialSeconds."""

import json
import re
import sys
from importlib.metadata import entry_points
from pathlib import Path

import yaml
from pydantic import BaseModel, ValidationError

from open_rigs.components.zapit import RAMP_DOWN_CONTEXT, bank_ramp_down_seconds, max_ramp_down_seconds, zapit_trial_seconds
from open_rigs.rigs.conditioned_task import DRAWN_CONTEXT

GROUP = "open_rigs.task"
TRIAL_SECONDS = "zapitTrialSeconds"  # the answer's ZapitDaq trial buffer, from the task's timing (answer)


def model() -> type[BaseModel]:
    """The environment's task parameters model, by its GROUP entry point; LookupError for none or several."""
    found = list(entry_points(group=GROUP))
    if len(found) != 1:
        raise LookupError(f"{len(found)} {GROUP} entry points in this environment ({', '.join(e.value for e in found)}); a task names one")
    return found[0].load()


def context(parameters: dict, bank: str | None = None) -> dict:
    """The validation context from files the task names, where they are (the rig): with optogenetics on, the largest
    ramp-down (RAMP_DOWN_CONTEXT) of the ZapitDaq session's bank when given, else of its Zapit conditions file. None
    readable: {}, and the workflow checks at Go. Optogenetics is on with the lab's enabled, or with either box of a task that
    chooses its Zapit per session (decision-making's zapitBonsai and zapitMatlab)."""
    opto = parameters.get("optogenetics") or {}
    if not (opto.get("enabled") or opto.get("zapitBonsai") or opto.get("zapitMatlab")):
        return {}
    if bank:
        try:
            return {RAMP_DOWN_CONTEXT: bank_ramp_down_seconds(bank)} if Path(bank).is_file() else {}
        except (OSError, ValueError, KeyError, json.JSONDecodeError):
            return {}
    path = str(opto.get("zapitConditionsPath") or "")
    if not path or not Path(path).is_file():
        return {}
    try:
        return {RAMP_DOWN_CONTEXT: max_ramp_down_seconds(path)}
    except (OSError, ValueError, yaml.YAMLError):
        return {}


def answer(parameters_model: type[BaseModel], parameters: dict, bank: str | None = None) -> dict:
    """The served answer to one check: {"problems": [...]}, with TRIAL_SECONDS when the values fit, Zapit-Bonsai
    (optogenetics.zapitBonsai) is ticked and the model gives its trigger_gap_seconds of the weighted combinations. Without
    optogenetics, on MATLAB Zapit, or from a task that does not give it: none, and the workflow keeps rig.yml's."""
    drawn: list[dict] = []
    said = problems(parameters_model, parameters, bank, drawn)
    gap = getattr(parameters_model, "trigger_gap_seconds", None)
    if said or not drawn or not (parameters.get("optogenetics") or {}).get("zapitBonsai") or gap is None:
        return {"problems": said}
    seconds = gap(drawn)
    return {"problems": said, **({} if seconds is None else {TRIAL_SECONDS: zapit_trial_seconds(seconds)})}


def problems(parameters_model: type[BaseModel], parameters: dict, bank: str | None = None, drawn: list | None = None) -> list[str]:
    """What the model refuses in a task's taskParameters, each in its words, its field's path before it when it has one.
    `drawn`, an empty list, is filled with the weighted combinations once they pass (a conditioned task's)."""
    try:
        parameters_model.model_validate(parameters, context={**context(parameters, bank), **({} if drawn is None else {DRAWN_CONTEXT: drawn})})
    except ValidationError as error:
        said = []
        for e in error.errors():
            where = ".".join(str(part) for part in e["loc"])
            message = _inner(e["msg"].removeprefix("Value error, "))
            said.append(f"{where}: {message}" if where else message)
        return said
    return []


INNER = re.compile(r"\n  (?:Value error, )?(.*?) \[type=")


def _inner(message: str) -> str:
    """A combination's refusal (conditioned_task: its condition, its values, then pydantic's whole error) as its condition
    and the model's own words."""
    inner = INNER.findall(message)
    return f"{message.split(' with {', 1)[0]}: {'; '.join(inner)}" if inner else message


def main() -> None:
    try:
        found = model()
    except (LookupError, ImportError) as error:
        print(json.dumps({"error": str(error)}), flush=True)
        sys.exit(2)
    if len(sys.argv) > 1:
        said = problems(found, yaml.safe_load(Path(sys.argv[1]).read_text(encoding="utf-8"))["taskParameters"])
        print("\n".join(said) or "fits")
        sys.exit(1 if said else 0)
    print(json.dumps({"model": f"{found.__module__}:{found.__qualname__}"}), flush=True)
    for line in sys.stdin:
        request = json.loads(line)
        print(json.dumps(answer(found, request["taskParameters"], request.get("zapitBank"))), flush=True)


if __name__ == "__main__":
    main()
