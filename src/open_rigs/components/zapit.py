"""Zapit, the optogenetics server a rig's tasks arm each trial."""
import re
from pathlib import Path

import yaml
from pydantic import Field
from swc.aeon.schema import BaseSchema

# How long before its trigger Zapit's armed reply must be in, in seconds (OpenRigs.Devices ZapitTrial.ReplyMarginSeconds):
# every task using Zapit leaves this much between arming Zapit and the trigger.
ZAPIT_REPLY_MARGIN_SECONDS = 0.2
# The longest Zapit ramp-down a task may ask for, in seconds (OpenRigs.Devices ZapitConditions.MaximumRampDownSeconds).
ZAPIT_MAX_RAMP_DOWN_SECONDS = 0.5
# The validation context key a task model reads its conditions file's largest ramp-down from (open_rigs.rigs.check_task
# puts it there on the rig, where the file is); without it the workflow checks the file at Go.
RAMP_DOWN_CONTEXT = "zapit_ramp_down_seconds"


def max_ramp_down_seconds(path: str | Path) -> float:
    """A Zapit conditions file's highest offRampDownDuration_ms over its stimLocationsNN conditions, in seconds
    (ZapitConditions.Check's MaxRampDownSeconds; the file's other checks are the workflow's, at Go). ValueError for a
    condition with none."""
    doc = yaml.safe_load(Path(path).read_text(encoding="utf-8")) or {}
    conditions = {k: v for k, v in doc.items() if re.fullmatch(r"stimLocations\d+", str(k))}
    missing = [k for k, v in conditions.items() if not isinstance(v, dict) or "offRampDownDuration_ms" not in v]
    if not conditions or missing:
        raise ValueError(f"the Zapit conditions file {path} has {'no conditions' if not conditions else ', '.join(missing) + ' with no offRampDownDuration_ms'}")
    return max(float(v["offRampDownDuration_ms"]) for v in conditions.values()) / 1000


class ZapitServer(BaseSchema):
    """TCP endpoint of the Zapit optogenetics server (tcpServer section of the Zapit settings file). The trigger line is the board's ZapitTrigger in wiring."""

    host: str = Field(default="127.0.0.1", description="Host name or IP address of the PC running Zapit.")
    port: int = Field(default=1488, ge=1, le=65535, description="TCP port the Zapit server listens on.")
    hardware_triggered: bool = Field(
        default=False,
        description="True: Zapit arms its DAQ on the TCP command and the ZapitTrigger line starts the stimulus. False: Zapit starts the stimulus on receipt of the command.",
    )
