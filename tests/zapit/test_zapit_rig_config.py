import pytest
import yaml

from open_rigs.zapit.rig_config import ZapitConfigError, ZapitRig, read_rig_zapit

BLOCK = {"device": "Dev1", "galvoXChannel": "ao0", "galvoYChannel": "ao1", "laserChannel": "ao2", "ledChannel": "ao3",
         "photodiodeChannel": "ai0", "triggerChannel": "PFI0", "sampleRate": 100000,
         "laserMinMaxMw": [0.0, 75.0], "laserMinMaxControlVolts": [0.0, 5.0], "ceilingMw": 20.0,
         "maxTrialSeconds": 3.0, "micronsPerPixel": 11.7, "voltsPerPixel": 0.0015,
         "invertXScanner": True, "safetyGate": {"layer": "software", "date": "2026-11-22"},
         "camera": {"serialNumber": "40123456"}}


def test_a_full_block_reads():
    r = ZapitRig.from_dict(BLOCK)
    assert r.device == "Dev1" and r.ceiling_mw == 20.0 and r.invert_x_scanner and not r.invert_y_scanner
    assert r.safety_gate.layer == "software" and r.safety_gate.date == "2026-11-22"
    assert r.laser().max_mw == 75.0
    assert r.camera.serial_number == "40123456" and r.camera.session_pfs_path == "calibration/Zapit-Session.pfs"


@pytest.mark.parametrize("key", [k for k in BLOCK if k not in ("invertXScanner",)])
def test_a_missing_required_field_is_refused_by_name(key):
    b = {k: v for k, v in BLOCK.items() if k != key}
    with pytest.raises(ZapitConfigError, match=key):
        ZapitRig.from_dict(b)


@pytest.mark.parametrize("change", [{"ceilingMw": -1}, {"ceilingMw": True}, {"sampleRate": "fast"}, {"laserMinMaxMw": [5, 1]},
                                    {"safetyGate": {"layer": "none"}}, {"safetyGate": {}}, {"extra": 1},
                                    {"invertXScanner": "yes"}, {"earlyStopLeadSeconds": 0.1}, {"triggerFilterSeconds": 1e-4},
                                    {"camera": {}}, {"camera": {"serialNumber": "1", "x": "y"}},
                                    {"camera": {"serialNumber": ""}}, {"invertXscanner": True}, {"device": ""}])
def test_bad_values_are_refused(change):
    with pytest.raises(ZapitConfigError):
        ZapitRig.from_dict({**BLOCK, **change})


def test_a_zero_ceiling_means_laser_disabled_and_reads():
    assert ZapitRig.from_dict({**BLOCK, "ceilingMw": 0}).ceiling_mw == 0.0


def test_read_from_a_rig_yml(tmp_path):
    p = tmp_path / "rig.yml"
    p.write_text(yaml.safe_dump({"rootPath": "C:/data", "zapit": BLOCK}))
    rig = read_rig_zapit(p)
    assert rig is not None and rig.microns_per_pixel == 11.7
    p.write_text(yaml.safe_dump({"rootPath": "C:/data"}))
    assert read_rig_zapit(p, require=False) is None
    with pytest.raises(ZapitConfigError, match="no zapit block"):
        read_rig_zapit(p)


def test_legacy_keys_are_ignored_and_new_keys_read():
    r = ZapitRig.from_dict({**BLOCK, "host": "localhost", "port": 1, "hardwareTriggered": True,
                            "blankingTimesMs": [0.4, -0.1, 0.2]})
    assert r.blanking_times_ms == (0.4, -0.1, 0.2)
    d = ZapitRig.from_dict(BLOCK)
    assert d.blanking_times_ms == (0.3, -0.12, 0.25)


@pytest.mark.parametrize("change", [{"blankingTimesMs": [0.3, 0.1]}, {"blankingTimesMs": "x"},
                                    {"blankingTimesMs": [0, 0, 0]}, {"hostname": "x"}])
def test_bad_new_keys_are_refused(change):
    with pytest.raises(ZapitConfigError):
        ZapitRig.from_dict({**BLOCK, **change})
