from pydantic import Field
from swc.aeon.schema import BaseSchema


class ProjectionCalibration(BaseSchema):
    """File-based calibration assets for mesh-mapped projection displays."""

    mesh_map_path: str = Field(
        examples=["C:\\RigConfigs\\MeshMap.csv"],
        description="Path to the mesh-mapping interpolation file (CSV)",
    )
    gamma_lut_path: str = Field(
        examples=["C:\\RigConfigs\\gammalut.bmp"],
        description="Path to the gamma lookup-table image (BMP)",
    )
