from typing import Optional

from pydantic import Field

from swc.aeon.schema import BaseSchema


class Rig(BaseSchema):
    root_path: str = Field(description="The root path on which to log all data.")
    rig_id: Optional[str] = Field(
        default=None,
        description="A short code naming this rig, kept when another computer takes over its job, for labs that build it into session IDs (for example the <RR> in ses-<NNN><RR>). Its form is the lab's to decide.",
    )
