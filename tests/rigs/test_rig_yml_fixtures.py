"""Four rigs' rig.yml files under the native Zapit block (fixtures/rig-yml): B1 and B2 converted, G1 and G2 with no
zapit block, everything else as the rigs have it."""
from pathlib import Path

import pytest
import yaml
from pydantic import ValidationError

from open_rigs.rigs.lab_rig import LabRig

FIXTURES = Path(__file__).parents[2] / "fixtures" / "rig-yml"


def load(rig: str) -> dict:
    return yaml.safe_load((FIXTURES / f"{rig}.yml").read_text(encoding="utf-8"))


@pytest.mark.parametrize("rig", ["B1", "B2", "G1", "G2"])
def test_fixture_loads(rig):
    loaded = LabRig.model_validate(load(rig))
    assert (loaded.zapit is not None) == rig.startswith("B")
    if loaded.zapit:
        assert loaded.zapit.camera.session_pfs_path == "calibration/Zapit-Session.pfs"


@pytest.mark.parametrize("rig", ["B2", "G1"])
def test_old_server_block_refused(rig):
    old = load(rig)
    old["zapit"] = {"host": "127.0.0.1", "port": 1488, "hardwareTriggered": True}
    with pytest.raises(ValidationError, match="zapit"):
        LabRig.model_validate(old)


def test_rising_laser_limits():
    rig = load("B2")
    rig["zapit"]["laserMinMaxMw"] = [50, 0]
    with pytest.raises(ValidationError, match="laserMinMaxMw must rise"):
        LabRig.model_validate(rig)
