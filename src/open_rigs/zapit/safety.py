"""The laser ceiling: ``ceiling_mw`` caps every control voltage open_rigs.zapit hands out, except the laser-curve
staircase (``laser_curve.staircase_volts``, which needs ``test_subject``). At or above the laser's maximum it
changes nothing. A ceiling of 0 means the laser is disabled: every laser volts value is 0 V."""
from __future__ import annotations

from open_rigs.zapit.laser import LaserCalibration


def laser_disabled(ceiling_mw: float) -> bool:
    return ceiling_mw <= 0


def max_control_volts(laser: LaserCalibration, ceiling_mw: float) -> float:
    if laser_disabled(ceiling_mw):
        return 0.0
    return laser.max_cv if ceiling_mw >= laser.max_mw else laser.mw_to_control(ceiling_mw, warn=None)


def laser_volts(laser: LaserCalibration, mw: float, ceiling_mw: float) -> float:
    """Control volts for ``mw``, never above the ceiling's; 0 V when the laser is disabled."""
    if laser_disabled(ceiling_mw):
        return 0.0
    return min(laser.mw_to_control(min(float(mw), ceiling_mw), warn=None), max_control_volts(laser, ceiling_mw))
