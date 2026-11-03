from pydantic import Field
from swc.aeon.schema import BaseSchema

import open_rigs.core.base as data_types
from open_rigs.core.artefacts import ArtefactPath
from open_rigs.core.calibration import Calibration


class MeshMap(Calibration):
    """A projection mesh map, stored as the CSV BonVision loads beside this descriptor."""

    azimuth_resolution: data_types.Int = Field(gt=0, description="Number of mesh points in azimuth.")
    elevation_resolution: data_types.Int = Field(gt=0, description="Number of mesh points in elevation.")
    mesh_path: ArtefactPath = Field(description="Relative path of the mesh CSV that BonVision loads.")


class ProjectionCorrection(BaseSchema):
    """Corrections applied to a projected display. A rig adds this next to its display when it has one."""

    mesh_map: ArtefactPath | None = Field(
        default=None,
        examples=["calibration/mesh.json"],
        description="Relative path of the MeshMap artefact that warps the image onto the projection surface.",
    )
    gamma: ArtefactPath | None = Field(
        default=None,
        examples=["calibration/gamma.json"],
        description="Relative path of the gamma CalibrationCurve (input level against luminance, LUT bitmap as payload).",
    )
