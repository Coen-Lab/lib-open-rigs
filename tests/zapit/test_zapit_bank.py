"""The bank against the contract fixture: points, and cycles x N plus the ramp tail reproduce the app's
``Zapit.trial_waveforms`` (made by make_contract_fixture.py) within float32 tolerance."""
import json
from typing import Any, TypedDict

import numpy as np
import pytest

from open_rigs.zapit import bank
from open_rigs.zapit import waveforms as wf
from open_rigs.zapit.laser import LaserCalibration, LaserFit
from open_rigs.zapit.stim_config import StimConfig
from open_rigs.zapit.transforms import Affine, ScannerMapping

POWERS = [2.0, 8.0, 40.0]


class Rig(TypedDict):
    mapping: ScannerMapping
    laser: LaserCalibration
    ref_stereotaxic: Any
    ref_sample: Any
    blanking: wf.BlankingParams
    sample_rate: float
    ceiling_mw: float
    powers_mw: list[float] | None


@pytest.fixture(scope="module")
def inputs(request):
    from pathlib import Path
    return json.loads((Path(__file__).parent / "data" / "contract_inputs.json").read_text())


def rig(i, ceiling_mw=1e9) -> Rig:
    mapping = ScannerMapping(i["microns_per_pixel"], i["volts_per_pixel"], i["invert_x"], i["invert_y"],
                             Affine(np.array(i["matrix"])))
    laser = LaserCalibration(i["laser_control_volts"], i["laser_mw"],
                             LaserFit.fit(np.array(i["laser_control"]), np.array(i["laser_sensor"])))
    return Rig(mapping=mapping, laser=laser, ref_stereotaxic=i["ref_stereotaxic"], ref_sample=i["ref_sample"],
               blanking=wf.BlankingParams(*i["blanking"]), sample_rate=i["sample_rate"], ceiling_mw=ceiling_mw,
               powers_mw=POWERS)


@pytest.fixture
def built(inputs, data_dir, tmp_path):
    js, _ = bank.build_bank(data_dir / "contract_conditions.yml", tmp_path, stamp="T", **rig(inputs))
    index, data = bank.read_bank(js)
    return index, data, tmp_path


def test_files_and_layout(built, data_dir):
    index, data, folder = built
    assert sorted(p.name for p in folder.iterdir()) == ["Settings_ZapitBank_T.bin", "Settings_ZapitBank_T.json"]
    assert data.scan.dtype == np.dtype("<f4") and data.scan.shape == (index["scan_rows"], 3)
    assert data.laser.shape == (index["laser_rows"],)
    assert index["conditions_name"] == "contract_conditions.yml" and len(index["conditions_sha256"]) == 64
    assert set(index["hashes"]) == {"scanner_fit", "laser_curve", "sample", "bin"}
    assert all(len(h) == 64 for h in index["hashes"].values())
    # What made it, beyond the hashed inputs: the code (its commit, as the version never moves) and the blanking.
    from open_rigs.zapit import __version__
    assert index["lib_zapit"].startswith(__version__.split("+")[0] + "+g")  # this checkout is a git one
    assert index["blanking_times_ms"] == pytest.approx(json.loads((data_dir / "contract_inputs.json").read_text())["blanking"])


def test_points_match_the_apps(built, inputs):
    index = built[0]
    for cond, expected in zip(index["conditions"], inputs["points_mm"]):
        np.testing.assert_allclose(cond["points_mm"], expected, atol=1e-9)


def test_trials_reproduce_the_apps_trial_waveforms(built, inputs):
    index, data, _ = built
    with np.load(__import__("pathlib").Path(__file__).parent / "data" / "contract_expected.npz") as z:
        for c, on, p, n in inputs["cases"]:
            got = bank.assemble(index, data, c, on, p, n)
            want = z[f"c{c}_{'on' if on else 'off'}_{p:g}_{n}"]
            assert got.shape == want.shape, (c, on, p, n)
            np.testing.assert_allclose(got, want, atol=1e-5, rtol=1e-6, err_msg=str((c, on, p, n)))


def test_trials_end_with_laser_and_led_at_zero(built, inputs):
    index, data, _ = built
    for c, on, p, n in inputs["cases"]:
        assert not bank.assemble(index, data, c, on, p, n)[-1, 2:].any()


def test_capped_power_is_reported(built):
    index = built[0]
    low = bank.find_variant(index, 1, True, 2.0)
    high = bank.find_variant(index, 1, True, 40.0)
    assert not low["power_capped"] and low["capped_power_mw"] == pytest.approx(2.0)
    assert high["power_capped"] and high["capped_power_mw"] < 40.0
    assert not bank.find_variant(index, 1, False)["power_capped"]


def test_ceiling_caps_below_the_laser_maximum(inputs, data_dir):
    config = StimConfig.load(data_dir / "contract_conditions.yml")
    index, data = bank.build_arrays(config, **rig(inputs, ceiling_mw=10.0))
    data = bank.split_bank(index, data)
    v = bank.find_variant(index, 1, True, 8.0)
    assert v["power_capped"] and v["capped_power_mw"] < 8.0
    laser = rig(inputs)["laser"]
    assert bank.assemble(index, data, 1, True, 8.0, 2)[:, 2].max() <= laser.mw_to_control(10.0, warn=None) + 1e-6


def test_a_zero_ceiling_disables_the_laser(inputs, data_dir):
    config = StimConfig.load(data_dir / "contract_conditions.yml")
    r = rig(inputs, ceiling_mw=0.0)
    r["laser"] = LaserCalibration([0.5, 5.0], [0.0, 75.0])   # a nonzero minimum volts must not leak through
    index, data = bank.build_arrays(config, **r)
    assert not np.frombuffer(data, dtype="<f4")[index["scan_rows"] * 3:].any()
    for c in index["conditions"]:
        for v in c["variants"]:
            assert v["laser_cycle"] is None and v["laser_tail"] is None and v["capped_power_mw"] == 0
    assert bank.assemble(index, bank.split_bank(index, data), 1, True, 8.0, 2)[:, 2].max() == 0


def test_laser_off_blocks_are_stored_once(built):
    index = built[0]
    off = [v for v in index["conditions"][0]["variants"] if not v["laser_on"]]
    assert len(off) == 1


def test_default_power_is_the_files(inputs, data_dir):
    config = StimConfig.load(data_dir / "contract_conditions.yml")
    r = rig(inputs)
    r["powers_mw"] = None
    index, _ = bank.build_arrays(config, **r)
    assert [v["power_mw"] for v in index["conditions"][0]["variants"]] == [2.0, 0.0]



def test_read_bank_refuses_a_changed_bin(built):
    folder = built[2]
    p = folder / "Settings_ZapitBank_T.bin"
    p.write_bytes(p.read_bytes()[:-4] + bytes(4) if p.read_bytes()[-4:] != bytes(4) else bytes(4) + p.read_bytes()[4:])
    with pytest.raises(ValueError, match="hash"):
        bank.read_bank(folder / "Settings_ZapitBank_T.json")


def real_conditions(n=8):
    out = []
    for i in range(n):
        two = i % 2
        ml = f"[{1.0 + i}, {-(1.0 + i)}]" if two else f"{1.0 + i}"
        ap = "[-1.0, -1.0]" if two else "-1.0"
        kind = "bilateral_points" if two else "unilateral_point"
        out.append(f"stimLocations{i + 1:02d}:\n  ML: {ml}\n  AP: {ap}\n  Class: stimulate\n  Type: {kind}\n"
                   f"  Attributes: {{laserPowerInMW: 3.0, offRampDownDuration_ms: 500.0}}\n")
    return "".join(out) + "stimModulationFreqHz: 40.0\n"


def test_real_task_bank_is_small(inputs, tmp_path):
    """A real task's case: 8 conditions of 1 or 2 points, one power, a 0.5 s ramp, 40 Hz, 100 kS/s."""
    r = rig(inputs)
    r["powers_mw"] = [3.0]
    path = tmp_path / "c.yml"
    path.write_text(real_conditions())
    js, binp = bank.build_bank(path, tmp_path / "out", stamp="T", **r)
    assert binp.stat().st_size < 1_000_000
    index = json.loads(js.read_text())
    assert index["laser_rows"] <= 2 * 50_000 + 2 * 2500   # two distinct ramp tails (1 and 2 points), not one per condition


def test_a_conditions_file_changed_while_read_is_refused(inputs, data_dir, tmp_path, monkeypatch):
    path = tmp_path / "c.yml"
    path.write_bytes((data_dir / "contract_conditions.yml").read_bytes())
    load = StimConfig.load
    monkeypatch.setattr(StimConfig, "load", lambda p: (load(p), path.write_bytes(path.read_bytes() + b"\n"))[0])
    with pytest.raises(ValueError, match="changed while the bank was built"):
        bank.build_bank(path, tmp_path / "out", stamp="T", **rig(inputs))


def test_source_reads_the_commit_of_a_git_installed_package(monkeypatch):
    """A package installed from git has no .git beside it: the commit is in PEP 610's direct_url.json."""
    from importlib import metadata

    import open_rigs.zapit as z

    class Dist:
        def read_text(self, name):
            assert name == "direct_url.json"
            return json.dumps({"url": "https://example.org/r.git", "vcs_info": {"vcs": "git", "commit_id": "0123456789abcdef0123"}})

    monkeypatch.setattr(metadata, "distribution", lambda name: Dist())
    monkeypatch.setattr(z, "__version__", "0.1.dev7+gabcdef1.d20261126")
    assert z.source() == "0.1.dev7+g0123456789ab"


def test_source_of_an_editable_install_falls_back_to_the_checkout(monkeypatch):
    from importlib import metadata

    import open_rigs.zapit as z

    class Dist:
        def read_text(self, name):
            return json.dumps({"url": "file:///x", "dir_info": {"editable": True}})

    monkeypatch.setattr(metadata, "distribution", lambda name: Dist())
    assert z.source().startswith(z.__version__.split("+")[0] + "+g")
