"""What each trial column means (data_dictionary.yml beside this file), for open_rigs.trials.

The names a workflow may use are no longer here: they are K:\\config\\device-dictionary.json and
K:\\config\\data-dictionary.json, edited from rig-setup's Repos page (DECISIONS.md, stage 2). This
file and its YAML leave in stage 3, when the meaning moves to each workflow repo and its sessions.
"""

import json
from pathlib import Path
from typing import Any, Dict

from ruamel.yaml import YAML

PATH = Path(__file__).with_name("data_dictionary.yml")


def load(path: Path = PATH) -> Dict[str, Any]:
    """The list as plain dicts and lists."""
    return json.loads(json.dumps(YAML(typ="safe").load(path.read_text(encoding="utf-8"))))
