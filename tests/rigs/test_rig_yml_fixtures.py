"""The four rigs' rig.yml as they are on the share (fixtures/rig-yml, byte copies: MATLAB Zapit's block on all four) load
unchanged, and so does a blue rig carrying ZapitDaq's fields beside MATLAB Zapit's (B2-both)."""
from pathlib import Path

import pytest
import yaml
from pydantic import ValidationError

from open_rigs.rigs.lab_rig import LabRig

FIXTURES = Path(__file__).parents[2] / "fixtures" / "rig-yml"


def load(rig: str) -> dict:
    return yaml.safe_load((FIXTURES / f"{rig}.yml").read_text(encoding="utf-8"))


@pytest.mark.parametrize("rig", ["B1", "B2", "G1", "G2"])
def test_the_domes_rig_yml_load_unchanged(rig):
    loaded = LabRig.model_validate(load(rig))
    assert loaded.zapit is not None and loaded.zapit.port == 1488 and not loaded.zapit.has_daq
    assert loaded.zapit.model_dump(by_alias=True, exclude_defaults=True).keys() <= {"host", "port", "hardwareTriggered"}


def test_a_blue_rig_carries_both():
    zapit = LabRig.model_validate(load("B2-both")).zapit
    assert zapit is not None and zapit.has_daq
    assert (zapit.host, zapit.port, zapit.hardware_triggered) == ("127.0.0.1", 1488, True)
    assert (zapit.device, zapit.trigger_channel, zapit.camera.session_pfs_path) == ("Dev1", "PFI0", "calibration/Zapit-Session.pfs")


def test_zapit_daq_s_fields_alone_load():
    rig = load("B2-both")
    for key in ("host", "port", "hardwareTriggered"):
        del rig["zapit"][key]
    assert LabRig.model_validate(rig).zapit.has_daq


def test_some_of_zapit_daq_s_fields_are_refused():
    rig = load("B2-both")
    del rig["zapit"]["ceilingMw"], rig["zapit"]["camera"]
    with pytest.raises(ValidationError, match="but not ceilingMw, camera"):
        LabRig.model_validate(rig)


def test_rising_laser_limits():
    rig = load("B2-both")
    rig["zapit"]["laserMinMaxMw"] = [50, 0]
    with pytest.raises(ValidationError, match="laserMinMaxMw must rise"):
        LabRig.model_validate(rig)
