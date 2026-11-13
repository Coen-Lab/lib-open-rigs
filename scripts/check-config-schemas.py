"""Each config\\*.yml of a workflow against its own schema (settings-files plan, audit M5): the one its
`# yaml-language-server: $schema=` line names, a `#/$defs/<Name>` fragment taking that definition;
else task*.yml against src\\DataSchemas\\*_task.json, valve-calibration.yml against calibration.json and
rig-template.yml against the other schema there. A task file the generated DeserializeFromYaml would
refuse at Go (an unknown or misspelt key) ended the experiment early; this finds it at commit.

    uv run --no-project --with jsonschema --with pyyaml python scripts\\check-config-schemas.py <workflow repo>

Prints one line a file; exit 0 when every file with a schema fits it. A file with none is said, not failed.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import Any

import jsonschema
import yaml

HEADER = re.compile(r"^#\s*yaml-language-server:\s*\$schema=(\S+)", re.MULTILINE)


def schema_for(config: Path, schemas: Path) -> tuple[Path | None, str]:
    """The schema file and the $defs name a config file is checked against."""
    text = config.read_text(encoding="utf-8-sig")
    named = HEADER.search(text)
    if named and not re.match(r"https?:", named.group(1)):
        file, _, fragment = named.group(1).partition("#")
        definition = fragment.rsplit("/", 1)[-1] if fragment.startswith("/$defs/") else ""
        return (config.parent / file).resolve(), definition
    found = sorted(schemas.glob("*.json"))
    if config.name.startswith("task"):
        pick = [p for p in found if p.stem.endswith("_task")]
    elif config.name == "valve-calibration.yml":
        pick = [p for p in found if p.name == "calibration.json"]
    elif config.name == "rig-template.yml":
        pick = [p for p in found if not p.stem.endswith("_task") and p.name != "calibration.json"]
    else:
        pick = []
    return (pick[0] if len(pick) == 1 else None), ""


def closed(node: Any) -> Any:
    """The schema with every object that lists its properties closed to others, as the generated
    DeserializeFromYaml is: it refuses an unknown key, which the schema (pydantic's) lets through."""
    if isinstance(node, dict):
        node = {k: closed(v) for k, v in node.items()}
        if isinstance(node.get("properties"), dict) and "additionalProperties" not in node:
            node["additionalProperties"] = False
    elif isinstance(node, list):
        node = [closed(v) for v in node]
    return node


def problem(config: Path, schema_path: Path, definition: str) -> str:
    """Why the schema refuses the file, naming the field; "" when it takes it."""
    schema = closed(json.loads(schema_path.read_text(encoding="utf-8-sig")))
    if definition:
        if definition not in schema.get("$defs", {}):
            return f"{schema_path.name} has no $defs/{definition}"
        schema = {"$schema": schema.get("$schema"), "$defs": schema["$defs"], "$ref": f"#/$defs/{definition}"}
    doc = yaml.safe_load(config.read_text(encoding="utf-8-sig"))
    errors = sorted(jsonschema.Draft202012Validator(schema).iter_errors(doc), key=lambda e: list(e.absolute_path))
    if not errors:
        return ""
    first = errors[0]
    where = ".".join(str(p) for p in first.absolute_path) or "the file"
    more = f" (and {len(errors) - 1} more)" if len(errors) > 1 else ""
    return f"{where}: {first.message}{more}"


def main(repo: Path) -> int:
    failed = 0
    configs = sorted((repo / "config").glob("*.yml"))
    for config in configs:
        schema_path, definition = schema_for(config, repo / "src" / "DataSchemas")
        if schema_path is None:
            print(f"  --    config\\{config.name}: no schema names it, so it is not checked")
            continue
        if not schema_path.is_file():
            print(f"  FAIL  config\\{config.name}: its schema {schema_path.name} is not there")
            failed += 1
            continue
        try:
            why = problem(config, schema_path, definition)
        except (OSError, ValueError, yaml.YAMLError) as error:
            why = f"could not be read: {str(error).splitlines()[0]}"
        if why:
            print(f"  FAIL  config\\{config.name} against {schema_path.name}: {why}")
            failed += 1
        else:
            print(f"  ok    config\\{config.name} fits {schema_path.name}{'#' + definition if definition else ''}")
    if not configs:
        print("  ok    no config\\*.yml")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
