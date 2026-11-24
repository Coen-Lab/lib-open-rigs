import pytest
from pydantic import ValidationError

from open_rigs.devices.lickety_split import LicketySplit


def test_thresholds_default_to_zero_so_nothing_is_written():
    device = LicketySplit(port_name="COM3")
    assert device.channel0_trigger_threshold == 0
    assert device.channel0_untrigger_threshold == 0


@pytest.mark.parametrize("field", ["channel0_trigger_threshold", "channel0_untrigger_threshold"])
def test_thresholds_above_a_byte_are_refused(field):
    with pytest.raises(ValidationError):
        LicketySplit.model_validate({"port_name": "COM3", field: 256})
