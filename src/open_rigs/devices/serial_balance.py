from pydantic import Field

import open_rigs.core.base as data_types
from open_rigs.core.calibration import CalibrationSchema
from open_rigs.devices.serial import SerialDevice


class SerialBalance(SerialDevice, CalibrationSchema):
    """A serial balance read by the platform's dispense calibration through `OpenRigs.Devices:SerialBalance.bonsai`.

    Extends `SerialDevice` with the balance's protocol.
    """

    port_name: str = Field(
        default="COMx", examples=["COM5"], description="The balance's serial port; set on the workflow at run time."
    )
    tare_command: str = Field(default="T", description="Command that tares the balance.")
    print_command: str = Field(default="IP", description="Command that makes the balance print one reading.")
    print_period_s: data_types.Double = Field(default=0.5, gt=0, description="How often a reading is requested, in seconds.")
    boot_delay_s: data_types.Double = Field(
        default=0.5, ge=0, description="Wait after opening the port before taring, in seconds."
    )
    tare_delay_s: data_types.Double = Field(
        default=1.5, ge=0, description="Wait after taring before the first reading is requested, in seconds."
    )
    unit_suffix: str = Field(
        default="g", description="Text that follows the number in a reading; the value is parsed up to it."
    )
    unstable_marker: str = Field(default="?", description="Text present in a reading while the balance has not settled.")
    ignore: str = Field(
        default="ES", description="Readings to discard, comma separated, such as the balance's error or echo strings."
    )
