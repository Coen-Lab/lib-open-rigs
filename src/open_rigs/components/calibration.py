from pathlib import PureWindowsPath

from pydantic import Field, ValidationInfo, field_validator
from swc.aeon.schema import BaseSchema

from open_rigs.core.artefacts import ArtefactPath


class ProjectionCalibration(BaseSchema):
    """File-based calibration assets for mesh-mapped projection displays."""

    mesh_map_path: ArtefactPath = Field(
        examples=["calibration/MeshMap.csv"],
        description="The mesh-mapping interpolation file (CSV), relative to rig.yml's folder. A full path stops the workflow at Start (OpenRigs.Core ResolveRigFile).",
    )
    gamma_lut_path: ArtefactPath = Field(
        examples=["calibration/gammalut.bmp"],
        description="The gamma lookup-table image (BMP), relative to rig.yml's folder. A full path stops the workflow at Start (OpenRigs.Core ResolveRigFile).",
    )

    @field_validator("mesh_map_path", "gamma_lut_path")
    @classmethod
    def _relative(cls, value: str, info: ValidationInfo) -> str:
        # ProjectorDisplay resolves each against rig.yml's folder and refuses a full path at Start.
        path = PureWindowsPath(value)
        if path.drive or path.root:
            key = cls.model_fields[info.field_name].alias or info.field_name
            raise ValueError(
                f"projectionCalibration.{key} is a full path, {value}: give it relative to rig.yml's folder, "
                f"as `{key}: calibration/{path.name}` (lab rig model, open_rigs.rigs.lab_rig)."
            )
        return value
