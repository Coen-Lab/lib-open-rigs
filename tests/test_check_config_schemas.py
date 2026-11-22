"""scripts/check-config-schemas.py reads a config file as the workflow's YamlDotNet does."""

import importlib.util
import json
from pathlib import Path

SCRIPT = Path(__file__).parents[1] / "scripts" / "check-config-schemas.py"
spec = importlib.util.spec_from_file_location("check_config_schemas", SCRIPT)
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)

SCHEMA = {"type": "object", "properties": {"rate": {"type": "number"}, "label": {"type": "string"}, "enabled": {"type": "boolean"}}}


def workflow(tmp_path: Path, text: str) -> Path:
    (tmp_path / "src" / "DataSchemas").mkdir(parents=True)
    (tmp_path / "src" / "DataSchemas" / "x_task.json").write_text(json.dumps(SCHEMA), encoding="utf-8")
    (tmp_path / "config").mkdir()
    (tmp_path / "config" / "task.yml").write_text(text, encoding="utf-8")
    return tmp_path


def test_an_exponent_without_a_dot_fits_a_number_and_a_date_like_value_a_string(tmp_path):
    assert check.main(workflow(tmp_path, "rate: 1e-3\nlabel: 2026-11-22\nenabled: yes\n")) == 0


def test_an_unknown_key_fails(tmp_path):
    assert check.main(workflow(tmp_path, "rate: 1.5\nrats: 2\n")) == 1
