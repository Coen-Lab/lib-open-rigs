"""The committed settings file of a dispense calibration.

Kept apart from `open_rigs.core.calibration` because it joins a procedure to a device model, and
the devices package imports the calibration module.
"""

from pydantic import Field

from open_rigs.core.calibration import CalibrationSchema, DispenseProcedure
from open_rigs.devices.serial_balance import SerialBalance


class DispenseCalibration(CalibrationSchema):
    """What an experiment repository commits as `config/<dispenser>-calibration.yml`.

    `OpenRigs.Core:DispenseCalibration.bonsai` deserialises it with the Core `DeserializeFromYaml`
    and runs the procedure; the balance port is set on the workflow at run time.
    """

    procedure: DispenseProcedure = Field(description="What to fire and how to weigh it.")
    balance: SerialBalance = Field(default=SerialBalance(), description="The balance's serial protocol.")
