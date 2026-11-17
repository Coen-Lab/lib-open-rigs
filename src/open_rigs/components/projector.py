from pydantic import Field
from swc.aeon.schema import BaseSchema

from open_rigs.core.artefacts import ArtefactPath


class Projector(BaseSchema):
    """File-based calibration assets for mesh-mapped projection displays."""

    mesh_path: ArtefactPath = Field(
        examples=["calibration/Projector-Mesh.csv"],
        description="The mesh-mapping interpolation file (CSV), relative to rig.yml's folder. A full path stops the workflow at Start (OpenRigs.Core ResolveRigFile).",
    )
    gamma_path: ArtefactPath = Field(
        examples=["calibration/Projector-Gamma.bmp"],
        description="The gamma lookup-table image (BMP), relative to rig.yml's folder. A full path stops the workflow at Start (OpenRigs.Core ResolveRigFile).",
    )
