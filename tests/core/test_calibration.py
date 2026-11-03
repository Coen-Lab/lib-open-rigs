from datetime import datetime, timezone

import pytest
from pydantic import ValidationError

from open_rigs.core.calibration import CalibrationCurve, CalibrationPoint

PROVENANCE = dict(
    calibrated_at=datetime(2026, 9, 26, 10, 30, tzinfo=timezone.utc),
    calibrated_by="Gerion",
    machine_name="GREEN-DOME-2",
)


def valve_curve() -> CalibrationCurve:
    return CalibrationCurve(
        **PROVENANCE,
        x_name="pulse_width",
        x_unit="ms",
        y_name="volume",
        y_unit="ul",
        points=[CalibrationPoint(x=20, y=1.1), CalibrationPoint(x=40, y=2.3), CalibrationPoint(x=60, y=3.4)],
    )


def test_curve_round_trips_through_json():
    curve = valve_curve()
    restored = CalibrationCurve.model_validate_json(curve.model_dump_json(by_alias=True))
    assert restored == curve


def test_curve_uses_camel_case_keys():
    data = valve_curve().model_dump(by_alias=True, mode="json")
    assert {"calibratedAt", "calibratedBy", "machineName", "xName", "yUnit", "points"} <= data.keys()


def test_curve_needs_at_least_two_points():
    with pytest.raises(ValidationError):
        CalibrationCurve(**PROVENANCE, x_name="x", x_unit="a", y_name="y", y_unit="b", points=[CalibrationPoint(x=1, y=1)])


def test_curve_has_no_payload_by_default():
    assert valve_curve().payload_path is None
