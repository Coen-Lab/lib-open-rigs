from typing import ClassVar
from pydantic import Field
from open_rigs.devices.harp import HarpDevice
import open_rigs.core.base as data_types

KEEP_OWN = "0 writes nothing, so the device keeps its own (writing 0 hangs the device)."


class LicketySplit(HarpDevice):
    """Represents a Harp LicketySplit device."""

    who_am_i: ClassVar[int] = 1400
    channel0_trigger_threshold: data_types.Byte = Field(
        default=0, description=f"Channel 0 trigger threshold, in percent of the board's running baseline signal: a signal below it counts as a lick (board default 90), written to the device at start. {KEEP_OWN}"
    )
    channel0_untrigger_threshold: data_types.Byte = Field(
        default=0, description=f"Channel 0 untrigger threshold, in percent of the board's running baseline signal: a signal above it ends a lick (board default 98), written to the device at start. {KEEP_OWN}"
    )
