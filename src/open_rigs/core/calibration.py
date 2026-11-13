"""Calibration artefacts and the settings of the platform's calibration procedures.

An artefact written into the machine's rig configuration directory and what a rig reads at startup.

Every model here is a `CalibrationSchema`: its C# type is generated once into `OpenRigs.Core`, and a
rig schema that references it binds to that type instead of generating a copy.
"""

from datetime import datetime
from typing import List

from pydantic import Field
from pydantic.json_schema import JsonSchemaValue
from swc.aeon.schema import BaseSchema

import open_rigs.core.base as data_types
from open_rigs.core.artefacts import ArtefactPath
from open_rigs.core.base import bind_typename

CSHARP_NAMESPACE = "OpenRigs.Core.Calibration"
"""The namespace the calibration types are generated into; `open_rigs._generators.calibration` writes it."""


class CalibrationSchema(BaseSchema):
    """A calibration model whose C# type lives in `OpenRigs.Core.Calibration`; any schema that references it binds to that type.

    The model tags its own definition with `x-sgen-typename`. A rig or tool schema generates
    no per-repo copy. The one generator that produces the Core types removes the tag from what it
    generates (`open_rigs._generators.calibration`).
    """

    @classmethod
    def __get_pydantic_json_schema__(cls, core_schema, handler) -> JsonSchemaValue:
        json_schema = super().__get_pydantic_json_schema__(core_schema, handler)
        bind_typename(handler.resolve_ref_schema(json_schema), f"{CSHARP_NAMESPACE}.{cls.__name__}")
        return json_schema


class Calibration(CalibrationSchema):
    """Provenance shared by every calibration artefact."""

    calibrated_at: datetime = Field(description="When the calibration was measured, in UTC.")
    calibrated_by: str = Field(description="Who ran the calibration.")
    machine_name: str = Field(
        description="Name of the machine the calibration was measured on. A rig warns when it is not the machine it runs on."
    )
    notes: str = Field(default="", description="Free-text notes about the calibration.")


class CalibrationPoint(CalibrationSchema):
    """One measured pair of a calibration curve."""

    x: data_types.Double = Field(description="The value the rig controls, in the unit of the x axis.")
    y: data_types.Double = Field(description="The measured value, in the unit of the y axis.")


class CalibrationCurve(Calibration):
    """A calibration measured as pairs, such as valve opening time against delivered volume.

    Written by the platform's DispenseCalibration procedure (valves, pumps) and read by a rig
    whose configuration points a CalibratedPulse at it; the rig fits the points at startup.
    """

    x_name: str = Field(examples=["pulse_width"], description="Name of the quantity the rig controls.")
    x_unit: str = Field(examples=["ms"], description="Unit of the x values.")
    y_name: str = Field(examples=["volume"], description="Name of the measured quantity.")
    y_unit: str = Field(examples=["ul"], description="Unit of the y values.")
    points: List[CalibrationPoint] = Field(
        min_length=2, description="Measured pairs; the fit is done at load, not stored."
    )
    payload_path: ArtefactPath | None = Field(
        default=None,
        description="Relative path of a file a consumer still needs alongside the points, such as a gamma LUT bitmap.",
    )


class DispenseSample(CalibrationSchema):
    """One size to measure: the dispenser fires `repeats` times at it and the gain is weighed once.
    """

    size: data_types.UShort = Field(
        gt=0, description="The size to fire, in the procedure's x unit: milliseconds for a valve, steps for a pump."
    )
    repeats: int = Field(default=10, ge=1, description="Pulses fired at this size before the gain is weighed.")
    pulse_gap_ms: data_types.UShort = Field(
        default=500, description="Off-to-on time between consecutive pulses, in milliseconds; long enough for the valve to seat."
    )


class DispenseProcedure(CalibrationSchema):
    """Settings of the dispense calibration: fire known sizes, weigh the gain, write a CalibrationCurve.

    Run by a calibration workflow; the fit is done afterwards in Python and written into the CalibrationCurve.
    """

    samples: List[DispenseSample] = Field(min_length=2, description="Sizes to measure, each fired and weighed in turn.")
    x_name: str = Field(default="pulse_width", description="Name of the quantity fired.")
    x_unit: str = Field(default="ms", description="Unit of the sizes.")
    priming_pulses: int = Field(
        default=5, ge=0, description="Pulses fired before the baseline weighing to fill the line."
    )
    priming_size: data_types.UShort | None = Field(
        default=None, description="Size of the priming pulses; the largest sample size when unset."
    )
    density_g_per_ml: data_types.Double = Field(
        default=1.0, gt=0, description="Density used to turn the weighed grams into microlitres. Water = 1.0."
    )
    settle_window: int = Field(
        default=10, ge=1, description="Consecutive stable readings, after the last pulse, before a weight is taken."
    )
    weigh_timeout_s: data_types.Double = Field(
        default=60.0, gt=0, description="Fail if the balance has not settled within this time, in seconds."
    )
    require_monotone: bool = Field(
        default=True,
        description="Refuse to write a curve that is not monotone, since a rig inverts it to look a volume up.",
    )
