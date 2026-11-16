"""Zapit, the optogenetics server a rig's tasks arm each trial."""
from pydantic import Field
from swc.aeon.schema import BaseSchema

# How long before its trigger Zapit's armed reply must be in, in seconds (OpenRigs.Devices ZapitTrial.ReplyMarginSeconds):
# every task using Zapit leaves this much between arming Zapit and the trigger.
ZAPIT_REPLY_MARGIN_SECONDS = 0.2
# The longest Zapit ramp-down a task may ask for, in seconds (OpenRigs.Devices ZapitConditions.MaximumRampDownSeconds).
ZAPIT_MAX_RAMP_DOWN_SECONDS = 0.5


class ZapitServer(BaseSchema):
    """TCP endpoint of the Zapit optogenetics server (tcpServer section of the Zapit settings file). The trigger line is the board's ZapitTrigger in wiring."""

    host: str = Field(default="127.0.0.1", description="Host name or IP address of the PC running Zapit.")
    port: int = Field(default=1488, ge=1, le=65535, description="TCP port the Zapit server listens on.")
    hardware_triggered: bool = Field(
        default=False,
        description="True: Zapit arms its DAQ on the TCP command and the ZapitTrigger line starts the stimulus. False: Zapit starts the stimulus on receipt of the command.",
    )
