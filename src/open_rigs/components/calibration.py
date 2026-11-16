from pydantic import Field
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
