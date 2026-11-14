from typing import Optional

from pydantic import Field
from swc.aeon.schema import BaseSchema


class LiveView(BaseSchema):
    """How OpenRigs.LiveView's StreamDevices streams this device; each setting left out keeps its default for the device's kind."""

    enabled: bool = Field(default=True, description="False: the device is not streamed at all.")
    rate: Optional[float] = Field(default=None, gt=0, le=1000, description="Messages per second (camera default 5, behavior board 10).")
    width: Optional[int] = Field(default=None, ge=1, le=4096, description="Width of the streamed frames, in pixels; video only (default 320).")
    height: Optional[int] = Field(default=None, ge=1, le=4096, description="Height of the streamed frames, in pixels; video only (default 240).")
