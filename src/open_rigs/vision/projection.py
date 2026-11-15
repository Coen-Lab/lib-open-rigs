from pydantic import Field

import open_rigs.core.base as data_types
from open_rigs.core.artefacts import ArtefactPath
from open_rigs.core.calibration import Calibration


class MeshMap(Calibration):
    """A projection mesh map, stored as the CSV BonVision loads beside this descriptor."""

    azimuth_resolution: data_types.Int = Field(gt=0, description="Number of mesh points in azimuth.")
    elevation_resolution: data_types.Int = Field(gt=0, description="Number of mesh points in elevation.")
    mesh_path: ArtefactPath = Field(description="Relative path of the mesh CSV that BonVision loads.")

