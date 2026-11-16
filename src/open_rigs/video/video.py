from typing import Union, Annotated
from pydantic import Field, RootModel
from swc.aeon.schema import BaseSchema

from open_rigs.core.base import DiscriminatorTypeMixin


class ArducamOV9180(DiscriminatorTypeMixin, BaseSchema):
    trigger_frequency: float = Field(
        default=50, ge=1, description="The frequency at which the camera is triggered (in Hz)."
    )
    device_index: int = Field(default=0, ge=0, description="The index of the device.")


class SpinnakerCamera(DiscriminatorTypeMixin, BaseSchema):
    # No trigger frequency: a camera runs at the rate of the board trigger its wiring line names it on.
    exposure_us: float = Field(default=15000, ge=0, description="The exposure time, in microseconds.")
    serial_number: str | None = Field(default="00000", description="The serial number of the camera.")
    gain_db: float = Field(default=1, ge=0, description="The sensor gain, in dB.")
    binning: int = Field(default=1, ge=1, description="The binning setting for the camera.")


class Camera(
    RootModel[Annotated[Union[ArducamOV9180, SpinnakerCamera], Field(discriminator="discriminator_type")]]
):
    """Discriminated camera configuration (Arducam or Spinnaker)."""

    pass
