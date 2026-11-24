from typing import ClassVar, Optional
from pydantic import Field
from open_rigs.devices.harp import HarpDevice
import open_rigs.core.base as data_types

KEEP_BOARDS_OWN = "Leave both out to keep the board's own thresholds."


class LicketySplit(HarpDevice):
    """Represents a Harp LicketySplit device."""

    who_am_i: ClassVar[int] = 1400
    # Byte: the board's registers 33 and 34 are U8, so a larger value would be written truncated.
    channel0_trigger_threshold: Optional[data_types.Byte] = Field(
        default=None,
        description="Percent of the board's running baseline signal below which Channel 0 starts a lick (board default 90), written to the board at start; left out, the board keeps its own.",
    )
    channel0_untrigger_threshold: Optional[data_types.Byte] = Field(
        default=None,
        description="Percent of the board's running baseline signal above which Channel 0 ends a lick (board default 98), written to the board at start; left out, the board keeps its own.",
    )

    def threshold_problem(self) -> Optional[str]:
        """What is wrong with the thresholds (0 hangs the board), or None; the rig names the device."""
        trigger, untrigger = self.channel0_trigger_threshold, self.channel0_untrigger_threshold
        if trigger is None and untrigger is not None:
            return f"channel0UntriggerThreshold is {untrigger} with no channel0TriggerThreshold. {KEEP_BOARDS_OWN}"
        if trigger is not None and trigger <= 0:
            return f"channel0TriggerThreshold is {trigger}, and it must be above 0 (0 hangs the board). {KEEP_BOARDS_OWN}"
        if trigger is not None and untrigger is not None and trigger >= untrigger:
            return (
                f"channel0TriggerThreshold {trigger} must be below channel0UntriggerThreshold {untrigger}. {KEEP_BOARDS_OWN}"
            )
        return None
