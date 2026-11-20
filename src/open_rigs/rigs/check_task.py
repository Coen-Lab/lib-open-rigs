"""A task's own Python model run on its task values, for the experiment launcher (app-rig-control) to refuse a value as it
is typed rather than at Go: every rule the model holds (the timing between fields, each combination, between trials)
said once, in the task's Python, never copied into its schema.

A task names its parameters model in its pyproject.toml:

    [project.entry-points."open_rigs.task"]
    parameters = "ucl_open_coen_dome_navigation.task:UclOpenCoenDomeNavigationTaskParameters"

`python -m open_rigs.rigs.check_task <task file>` prints the file's problems (exit 1 on any). With no file it serves:
its first line says {"model": ...} (or {"error": ...} and it exits), then each stdin line {"taskParameters": {...}} gets
one stdout line {"problems": [...]}, [] when the values fit, so one process answers every check (importing a model
takes seconds, a check milliseconds)."""

import json
import re
import sys
from importlib.metadata import entry_points
from pathlib import Path

import yaml
from pydantic import BaseModel, ValidationError

GROUP = "open_rigs.task"


def model() -> type[BaseModel]:
    """The environment's task parameters model, by its GROUP entry point; LookupError for none or several."""
    found = list(entry_points(group=GROUP))
    if len(found) != 1:
        raise LookupError(f"{len(found)} {GROUP} entry points in this environment ({', '.join(e.value for e in found)}); a task names one")
    return found[0].load()


def problems(parameters_model: type[BaseModel], parameters: dict) -> list[str]:
    """What the model refuses in a task's taskParameters, each in its words, its field's path before it when it has one."""
    try:
        parameters_model.model_validate(parameters)
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
        print(json.dumps({"problems": problems(found, json.loads(line)["taskParameters"])}), flush=True)


if __name__ == "__main__":
    main()
