"""Generates the lab rig model's schema and C# classes (`uv run regenerate-lab-rig`).

Writes `schemas/lab_rig.json` from `open_rigs.rigs.lab_rig.LabRig` and runs Bonsai.Sgen on it into
`src/OpenRigs.Devices/Rigs/LabRig.Generated.cs`, namespace `OpenRigs.Devices.Rigs`: every lab
workflow reads its rig.yml with these classes, so no workflow generates or keeps its own.
"""

import json
import subprocess
from pathlib import Path

import pydantic

from open_rigs.rigs.lab_rig import LabRig

SCHEMA_FILE = Path("src/open_rigs/schemas/lab_rig.json")
OUTPUT_DIR = Path("src/OpenRigs.Devices/Rigs")
OUTPUT_NAME = "LabRig.Generated.cs"
CSHARP_NAMESPACE = "OpenRigs.Devices.Rigs"


def build_schema() -> dict:
    """The lab rig model's schema, as the generated C# reads rig.yml."""
    return pydantic.RootModel[LabRig].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )


def main() -> None:
    """Writes the schema and generates the C# classes. Run from the repository root."""
    SCHEMA_FILE.write_text(json.dumps(build_schema(), indent=2))
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
