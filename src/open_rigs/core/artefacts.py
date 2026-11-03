"""References from a rig configuration to files in its configuration directory.

A rig configuration names the calibration artefacts it uses on the devices they calibrate,
never in a central index, so the platform stays agnostic of what hardware a rig has. Tooling
that needs every reference (a deployment check, a startup check, a session snapshot) finds
them with `rig_artefacts` instead.
"""

import types
import typing
from typing import Annotated, Any

from pydantic import BaseModel, Field, RootModel
from pydantic.fields import FieldInfo

__all__ = ["ArtefactPath", "rig_artefacts"]

SCHEMA_TAG = "x-rig-artefact"


class _ArtefactMarker:
    """Marks a string field as a path relative to the rig configuration directory."""

    def __repr__(self) -> str:
        return "ArtefactPath"


_MARKER = _ArtefactMarker()

ArtefactPath = Annotated[str, _MARKER, Field(json_schema_extra={SCHEMA_TAG: True})]
"""A file path relative to the rig configuration directory, such as `calibration/valve-DO1.json`.

It stays a plain string in generated code; rig workflows resolve it with OpenRigs.Core's
ResolveRigFile against the directory ResolveRigConfigDirectory returns.
"""


def rig_artefacts(model: BaseModel) -> dict[str, str]:
    """Lists every artefact a configuration references.

    Walks nested models, discriminated unions, lists and dictionaries, and returns the
    relative path of each `ArtefactPath` field that is set, keyed by its location in the
    configuration as written in YAML, for example
    `behaviorBoard.pulseController.outputs.DO1.artefact`.
    """
    found: dict[str, str] = {}
    _walk(model, "", found)
    return found


def _walk(value: Any, path: str, found: dict[str, str]) -> None:
    if isinstance(value, RootModel):
        _walk(value.root, path, found)
    elif isinstance(value, BaseModel):
        for name, info in type(value).model_fields.items():
            item = getattr(value, name)
            key = _join(path, info.alias or name)
            if _is_artefact(info):
                if item is not None:
                    found[key] = item
            else:
                _walk(item, key, found)
    elif isinstance(value, dict):
        for name, item in value.items():
            _walk(item, _join(path, str(name)), found)
    elif isinstance(value, (list, tuple)):
        for index, item in enumerate(value):
            _walk(item, f"{path}[{index}]", found)


def _join(path: str, name: str) -> str:
    return f"{path}.{name}" if path else name


def _is_artefact(info: FieldInfo) -> bool:
    # A required ArtefactPath has its metadata moved onto the field; an optional one keeps it
    # inside the union of the annotation.
    return any(item is _MARKER for item in info.metadata) or _has_marker(info.annotation)


def _has_marker(annotation: Any) -> bool:
    origin = typing.get_origin(annotation)
    if origin is Annotated:
        return any(item is _MARKER for item in typing.get_args(annotation)[1:])
    if origin is typing.Union or origin is types.UnionType:
        return any(_has_marker(arg) for arg in typing.get_args(annotation))
    return False
