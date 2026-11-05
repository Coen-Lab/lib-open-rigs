"""The lab's data dictionary (data_dictionary.yml beside this file), and its JSON copy for rig-setup.

    python -m open_rigs.data_dictionary --json K:\\config\\data-dictionary.json

writes the list as JSON, which rig-setup's machines window reads without a YAML reader;
scripts\\update-workflows.ps1 does this on every run.
"""

import argparse
import json
import os
from pathlib import Path
from typing import Any, Dict

from ruamel.yaml import YAML

PATH = Path(__file__).with_name("data_dictionary.yml")


def load(path: Path = PATH) -> Dict[str, Any]:
    """The list as plain dicts and lists."""
    return json.loads(json.dumps(YAML(typ="safe").load(path.read_text(encoding="utf-8"))))


def write_json(target: Path, path: Path = PATH) -> None:
    """Writes the list as JSON beside the target, then renames it over it, so no reader sees half a file."""
    tmp = target.with_name(target.name + ".tmp")
    tmp.write_text(json.dumps(load(path), indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, target)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--json", type=Path, required=True, help="Where to write the JSON copy.")
    write_json(parser.parse_args().json)
