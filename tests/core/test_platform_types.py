"""The platform calibration types bind to OpenRigs.Core in every schema but the one that generates them."""

import pydantic
from swc.aeon.schema import BaseSchema

from open_rigs._generators.calibration import PLATFORM_TYPES, build_schema
from open_rigs.core import yaml as rig_yaml
from open_rigs.core.calibration import CalibrationCurve, DispenseProcedure, DispenseSample
from open_rigs.core.dispense import DispenseCalibration
from open_rigs.devices.behavior_board import CalibratedPulse
from open_rigs.devices.serial_balance import SerialBalance


class Rig(BaseSchema):
    """Stands in for an experiment's rig model that references platform types."""

    curve: CalibrationCurve
    valve: CalibratedPulse


def rig_schema() -> dict:
    return pydantic.RootModel[Rig].model_json_schema(by_alias=True, mode="serialization")


def test_rig_schema_binds_platform_types_to_core():
    defs = rig_schema()["$defs"]
    assert defs["CalibrationCurve"]["x-sgen-typename"] == "OpenRigs.Core.Calibration.CalibrationCurve"
    assert defs["CalibrationPoint"]["x-sgen-typename"] == "OpenRigs.Core.Calibration.CalibrationPoint"


def test_rig_types_themselves_are_not_bound():
    defs = rig_schema()["$defs"]
    assert "x-sgen-typename" not in defs["CalibratedPulse"]


def test_platform_generator_emits_definitions_instead_of_bindings():
    defs = build_schema()["$defs"]
    for model in PLATFORM_TYPES:
        assert "x-sgen-typename" not in defs[model.__name__], model.__name__
    # Nested platform types are generated in the same run, so they are untagged too.
    for name in ("CalibrationPoint", "LinearFit", "DispenseSample", "DispenseProcedure", "SerialBalance"):
        assert "x-sgen-typename" not in defs[name], name


def test_generator_leaves_the_models_bound_for_everyone_else():
    build_schema()
    assert "x-sgen-typename" in rig_schema()["$defs"]["CalibrationCurve"]


def test_calibrated_pulse_names_only_its_artefact():
    pulse = CalibratedPulse.model_validate({"artefact": "calibration/valve-DO1.json"})
    assert pulse.model_dump(by_alias=True, mode="json") == {
        "discriminatorType": "CalibratedPulse",
        "artefact": "calibration/valve-DO1.json",
    }


def test_dispense_procedure_defaults():
    procedure = DispenseProcedure(samples=[DispenseSample(size=20), DispenseSample(size=60)])
    assert procedure.priming_size is None  # the procedure primes at the largest sample size
    assert procedure.priming_pulses == 5


def test_balance_port_is_a_placeholder_until_run_time():
    assert SerialBalance().port_name == "COMx"
    assert SerialBalance().baud_rate == 9600


def test_dispense_calibration_round_trips_through_yaml(tmp_path):
    settings = DispenseCalibration(
        procedure=DispenseProcedure(samples=[DispenseSample(size=20, repeats=5), DispenseSample(size=40)]),
        balance=SerialBalance(print_period_s=1.0),
    )
    path = tmp_path / "valve-calibration.yml"
    rig_yaml.save(settings, path)
    text = path.read_text(encoding="utf-8")
    assert "pulseGapMs" in text and "printPeriodS" in text
    # swc.aeon's BaseSchema links each child to its container, so nested models are compared by dump.
    restored = rig_yaml.load(DispenseCalibration, path)
    assert restored.model_dump(mode="json") == settings.model_dump(mode="json")
