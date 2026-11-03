"""Generates the calibration types into OpenRigs.Core (`uv run regenerate-calibration`).

Writes `schemas/calibration.json` and runs Bonsai.Sgen on it into
`src/OpenRigs.Core/Calibration/Calibration.Generated.cs`. The `x-sgen-typename` tags that every
`CalibrationSchema` model carries are removed here, which makes this the one place the types
are generated. `CalibrationModel` keeps its tag to the hand-written enum.
"""

import json
import subprocess
from pathlib import Path
from typing import Union

import pydantic

from open_rigs.components.audio import SpeakerFilter
from open_rigs.core.calibration import CSHARP_NAMESPACE, CalibrationCurve
from open_rigs.core.dispense import DispenseCalibration
from open_rigs.vision.projection import MeshMap

SCHEMA_FILE = Path("src/open_rigs/schemas/calibration.json")
OUTPUT_DIR = Path("src/OpenRigs.Core/Calibration")
OUTPUT_NAME = "Calibration.Generated.cs"

# The artefacts a rig reads and the settings the procedures take; one root, one C# file.
PLATFORM_TYPES = (CalibrationCurve, MeshMap, SpeakerFilter, DispenseCalibration)


def build_schema() -> dict:
    """The schema of the calibration types, untagged so sgen generates them."""
    model = pydantic.RootModel[Union[PLATFORM_TYPES]]  # type: ignore[valid-type]
    schema = model.model_json_schema(by_alias=True, mode="serialization", union_format="primitive_type_array")
    for definition in schema["$defs"].values():
        if definition.get("x-sgen-typename", "").startswith(CSHARP_NAMESPACE + "."):
            del definition["x-sgen-typename"]
    schema["title"] = "CalibrationTypes"  # the root class sgen generates
    return schema


def export_schema(path: Path) -> None:
    """Writes the schema beside a rig's own, for editor validation of its calibration settings YAML."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(build_schema(), indent=2))


def main() -> None:
    """Writes the schema and generates the C# types. Run from the repository root."""
    export_schema(SCHEMA_FILE)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "dotnet", "bonsai.sgen", str(SCHEMA_FILE),
            "--namespace", CSHARP_NAMESPACE,
            "--output", str(OUTPUT_DIR),
            "--name", OUTPUT_NAME,
            "--serializer", "json",
            "--serializer", "yaml",
        ],
        check=True,
    )


if __name__ == "__main__":
    main()
