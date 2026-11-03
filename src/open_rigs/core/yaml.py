from pathlib import Path
from typing import TypeVar

from pydantic import BaseModel
from pydantic_yaml import parse_yaml_file_as, to_yaml_str

T = TypeVar("T", bound=BaseModel)

SCHEMA_DIRECTIVE = "# yaml-language-server: $schema="


def load(model_type: type[T], path: str | Path) -> T:
    """Load and validate a YAML configuration file as the given model type."""
    return parse_yaml_file_as(model_type, path)


def save(model: BaseModel, path: str | Path, schema_path: str | None = None) -> None:
    """Save a model as a camelCase-keyed YAML file, optionally with a $schema editor directive."""
    header = f"{SCHEMA_DIRECTIVE}{schema_path}\n" if schema_path is not None else ""
    Path(path).write_text(header + to_yaml_str(model, by_alias=True), encoding="utf-8")
