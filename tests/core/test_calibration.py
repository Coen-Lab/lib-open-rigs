import json
from datetime import datetime, timezone
from pathlib import Path

import pytest
from pydantic import ValidationError

from open_rigs.core.calibration import CalibrationCurve, CalibrationPoint, LinearFit

FIXTURE = Path(__file__).parents[2] / "fixtures" / "calibration" / "valve-DO1.json"

PROVENANCE = dict(
    calibrated_at=datetime(2026, 9, 26, 10, 30, tzinfo=timezone.utc),
    calibrated_by="Experimenter",
    machine_name="RIG-NAME",
)


def valve_curve() -> CalibrationCurve:
    return CalibrationCurve(
        **PROVENANCE,
        x_name="pulse_width",
        x_unit="ms",
        y_name="volume",
        y_unit="ul",
        points=[CalibrationPoint(x=20, y=1.1), CalibrationPoint(x=40, y=2.3), CalibrationPoint(x=60, y=3.4)],
        pulse_gap_ms=100,
        linear_fit=LinearFit(slope=0.0575, intercept=0.0, min_x=20, max_x=60),
    )


def test_curve_round_trips_through_json():
    curve = valve_curve()
    restored = CalibrationCurve.model_validate_json(curve.model_dump_json(by_alias=True))
    assert restored.model_dump() == curve.model_dump()


def test_curve_uses_camel_case_keys():
    data = valve_curve().model_dump(by_alias=True, mode="json")
    assert {"calibratedAt", "calibratedBy", "machineName", "xName", "yUnit", "points", "pulseGapMs", "linearFit"} <= data.keys()
    assert data["linearFit"].keys() == {"slope", "intercept", "minX", "maxX"}


def test_curve_needs_at_least_two_points():
    data = valve_curve().model_dump(by_alias=True, mode="json")
    data["points"] = data["points"][:1]
    with pytest.raises(ValidationError):
        CalibrationCurve.model_validate(data)


@pytest.mark.parametrize("key", ["pulseGapMs", "linearFit"])
def test_a_file_without_the_gap_or_the_fit_is_refused(key):
    data = json.loads(FIXTURE.read_text(encoding="utf-8"))
    del data[key]
    with pytest.raises(ValidationError, match=key):
        CalibrationCurve.model_validate(data)


def test_curve_has_no_payload_by_default():
    assert valve_curve().payload_path is None


def test_fixture_is_a_valid_curve_whose_fit_is_the_least_squares_line_of_its_points():
    curve = CalibrationCurve.model_validate_json(FIXTURE.read_text(encoding="utf-8"))
    xs = [p.x for p in curve.points]
    ys = [p.y for p in curve.points]
    n = len(xs)
    mean_x, mean_y = sum(xs) / n, sum(ys) / n
    slope = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys)) / sum((x - mean_x) ** 2 for x in xs)
    fit = curve.linear_fit
    assert fit.slope == pytest.approx(slope)
    assert fit.intercept == pytest.approx(mean_y - slope * mean_x)
    assert (fit.min_x, fit.max_x) == (min(xs), max(xs))
    assert (curve.x_unit, curve.y_unit) == ("ms", "ul")


def test_fixture_round_trips_unchanged():
    data = json.loads(FIXTURE.read_text(encoding="utf-8"))
    assert CalibrationCurve.model_validate(data).model_dump(by_alias=True, mode="json") == data | {
        "calibratedAt": "2026-09-26T10:30:00Z"
    }


@pytest.mark.parametrize(
    "fit",
    [
        dict(slope=0.05, intercept=0.0, min_x=60, max_x=20),  # range backwards
        dict(slope=0.0, intercept=1.0, min_x=20, max_x=60),  # flat
        dict(slope=0.05, intercept=-2.0, min_x=20, max_x=60),  # no volume at the shortest width
        dict(slope=0.05, intercept=0.0, min_x=0, max_x=60),  # a zero-width pulse
    ],
)
def test_linear_fit_refuses_a_range_it_cannot_plan_in(fit):
    with pytest.raises(ValidationError):
        LinearFit(**fit)
