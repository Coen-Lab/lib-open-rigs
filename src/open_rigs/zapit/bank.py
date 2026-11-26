"""The bank: every waveform a task can need, built once, so the Bonsai node only selects and concatenates.

``build_bank`` writes ``Settings_ZapitBank_<DateTime>.json`` (the index) and ``.bin`` (float32 little-endian): a scan
section (galvo X, galvo Y, LED, three columns interleaved) then a laser section (control volts, one column). The scan
cycle is stored once per condition, the laser cycle and ramp tail per condition x power (laser on) with identical
blocks stored once, so the bank stays small (a ramp tail is one column, shared by conditions alike in points, ramp
and ephys). Laser and LED end at 0 V, the galvos hold their last position. ``assemble`` is the reference for the node.

No maths of its own: the cycle is ``waveforms.one_cycle``, the ramp is ``waveforms.finite_trial``, and the laser line
is scaled as ``Zapit.trial_waveforms`` scales it. ``ceiling_mw`` additionally caps every power; at or above the
laser's maximum it changes nothing.
"""
from __future__ import annotations

import datetime as _dt
import hashlib
import json
from pathlib import Path
from typing import NamedTuple

import numpy as np
from numpy.typing import ArrayLike

from open_rigs.zapit import source, waveforms as wf
from open_rigs.zapit.laser import LaserCalibration
from open_rigs.zapit.safety import laser_disabled, max_control_volts
from open_rigs.zapit.stim_config import StimConfig
from open_rigs.zapit.transforms import Polynomial, ScannerMapping, rotate_and_scale

SCAN_CHANNELS = 3   # galvo X, galvo Y, LED
DTYPE = "<f4"


def _canonical(obj) -> str:
    def default(o):
        if isinstance(o, np.ndarray):
            return o.tolist()
        if isinstance(o, (np.floating, np.integer)):
            return o.item()
        raise TypeError(type(o))
    return json.dumps(obj, sort_keys=True, default=default, separators=(",", ":"))


def digest(obj) -> str:
    return hashlib.sha256(_canonical(obj).encode()).hexdigest()


def scanner_fit_hash(mapping: ScannerMapping) -> str:
    t = mapping.transform
    if t is None:
        fit = None
    elif isinstance(t, Polynomial):
        fit = {"order": t.order, "forward": t.forward_coef, "inverse": t.inverse_coef}
    else:
        fit = t.to_dict()
    return digest({"microns_per_pixel": mapping.microns_per_pixel, "volts_per_pixel": mapping.volts_per_pixel,
                   "invert_x": mapping.invert_x, "invert_y": mapping.invert_y, "fit": fit})


def laser_fit_hash(laser: LaserCalibration) -> str:
    return digest({"control": [laser.min_cv, laser.max_cv], "mw": [laser.min_mw, laser.max_mw],
                   "fit": None if laser.fit is None else laser.fit.to_dict()})


def sample_hash(ref_stereotaxic, ref_sample) -> str:
    return digest({"stereotaxic": np.asarray(ref_stereotaxic, float), "sample": np.asarray(ref_sample, float)})




def _scaled_cycle(cycle: np.ndarray, peak_mw: float, laser_on: bool, laser: LaserCalibration, ceiling_mw: float):
    """The laser column in control volts, as ``Zapit.trial_waveforms`` does it, with the ceiling on top.
    Returns the cycle, whether a limit applied, and the factor the average power was reduced by."""
    limit_mw = min(laser.max_mw, ceiling_mw)
    cycle = cycle.copy()
    if laser_disabled(ceiling_mw):
        cycle[:, 2] = 0.0
        return cycle, laser_on, 0.0
    capped = laser_on and peak_mw > limit_mw
    factor = min(1.0, limit_mw / peak_mw) if capped else 1.0
    peak = min(peak_mw, limit_mw)
    cycle[:, 2] *= laser.mw_to_control(peak, warn=None) if laser_on else 0.0
    max_cv = max_control_volts(laser, ceiling_mw)
    top = cycle[:, 2].max()
    if top > max_cv:
        cycle[:, 2] *= max_cv / top
        capped = True
        factor *= max_cv / top
    return cycle, bool(capped), float(factor)


def _variant(cycle_volts, rate, ramp_ms, laser_on, peak_mw, laser, ceiling_mw):
    """The laser column of one cycle and of the ramp tail (one zero row when there is no ramp), the tail's length
    in cycles (0: no ramp) and the rows the tail replaces at the end of the body."""
    cycle, capped, factor = _scaled_cycle(cycle_volts, peak_mw, laser_on, laser, ceiling_mw)
    n = cycle.shape[0]
    full = wf.finite_trial(cycle, rate, n / rate, ramp_ms)   # exactly one cycle, then the ramp
    if not laser_on:
        full[:, 2] = 0
    if full.shape[0] > n:
        return full[:n, 2], full[n:, 2], (full.shape[0] - n) // n, 0, capped, factor
    return full[:n, 2], np.zeros(1), 0, 1, capped, factor    # no ramp: only the final sample is 0 V


class _Blob:
    """float32 blocks of ``width`` columns appended to one buffer; an identical block is stored once."""

    def __init__(self, width: int):
        self.width = width
        self.parts: list[bytes] = []
        self.rows = 0
        self.seen: dict[bytes, int] = {}

    def add(self, a: ArrayLike) -> dict:
        a = np.asarray(a).reshape(-1, self.width)
        raw = np.ascontiguousarray(a, dtype=DTYPE).tobytes()
        if raw not in self.seen:
            self.seen[raw] = self.rows
            self.parts.append(raw)
            self.rows += a.shape[0]
        return {"offset": self.seen[raw], "length": int(a.shape[0])}

    def tobytes(self) -> bytes:
        return b"".join(self.parts)


def build_arrays(config: StimConfig, mapping: ScannerMapping, laser: LaserCalibration, ref_stereotaxic,
                 ref_sample, blanking: wf.BlankingParams, sample_rate: float, ceiling_mw: float,
                 powers_mw=None) -> tuple[dict, bytes]:
    """The index (without file names or the conditions hash) and the bin's bytes.

    ``powers_mw``: the powers the task can draw; None or empty takes each condition's own power from the file.
    The scan columns (galvo X, galvo Y, LED) are stored once per condition; only the laser column differs by
    power, and identical laser blocks (laser off; conditions alike in points, ramp and ephys) are stored once.
    """
    rate = float(sample_rate)
    scan, lasers = _Blob(SCAN_CHANNELS), _Blob(1)
    conditions = []
    for i, cond in enumerate(config.conditions):
        sample_mm = rotate_and_scale(cond.points, ref_stereotaxic, ref_sample)
        volts = mapping.mm_to_volts(sample_mm)
        cycle = wf.one_cycle(volts, rate, config.modulation_hz, blanking, cond.ephys)
        scan_cycle = cycle[:, [0, 1, 3]]
        wanted = sorted({float(p) for p in powers_mw}) if powers_mw else [float(cond.laser_power_mw)]
        variants = []
        for laser_on, powers in ((True, wanted), (False, [0.0])):
            for avg in powers:
                peak = config.peak_power_mw(i, blanking.blanking_time_ms, avg)
                lc, lt, tail_cycles, replaces, capped, factor = _variant(cycle, rate, cond.rampdown_ms, laser_on,
                                                                          peak, laser, ceiling_mw)
                variants.append({"laser_on": laser_on, "power_mw": avg, "capped_power_mw": avg * factor,
                                 "power_capped": capped, "laser_cycle": lasers.add(lc) if lc.any() else None,
                                 "laser_tail": lasers.add(lt) if lt.any() else None, "tail_cycles": tail_cycles,
                                 "tail_replaces": replaces})
        # The trial's last row: the last cycle row's galvos with the LED at 0 (every laser tail ends at 0 too).
        end = [[scan_cycle[-1, 0], scan_cycle[-1, 1], 0.0]]
        conditions.append({"condition": i + 1, "points_mm": sample_mm.tolist(),
                           "stereotaxic_mm": cond.points.tolist(), "n_points": cond.n_points,
                           "scan_cycle": scan.add(scan_cycle), "scan_end": scan.add(end), "variants": variants})
    data = scan.tobytes() + lasers.tobytes()
    index = {"version": 2, "lib_zapit": source(), "sample_rate": rate, "modulation_hz": float(config.modulation_hz),
             "ceiling_mw": float(ceiling_mw),
             "blanking_times_ms": [blanking.blanking_time_ms, blanking.onset_shift_ms, blanking.offset_shift_ms],
             "scan_channels": ["galvo_x", "galvo_y", "led"],
             "dtype": "float32 little-endian",
             "layout": "scan section (scan_rows x 3, interleaved) then laser section (laser_rows x 1)",
             "scan_rows": scan.rows, "laser_rows": lasers.rows,
             "hashes": {"scanner_fit": scanner_fit_hash(mapping), "laser_curve": laser_fit_hash(laser),
                        "sample": sample_hash(ref_stereotaxic, ref_sample),
                        "bin": hashlib.sha256(data).hexdigest()},
             "conditions": conditions}
    return index, data


def build_bank(conditions_path, out_dir, mapping: ScannerMapping, laser: LaserCalibration, ref_stereotaxic,
               ref_sample, blanking: wf.BlankingParams, sample_rate: float, ceiling_mw: float, powers_mw=None,
               stamp: str | None = None) -> tuple[Path, Path]:
    """Write ``Settings_ZapitBank_<stamp>.json`` and ``.bin`` into ``out_dir``; returns both paths."""
    conditions_path = Path(conditions_path)
    sha256 = hashlib.sha256(conditions_path.read_bytes()).hexdigest()
    config = StimConfig.load(conditions_path)
    if hashlib.sha256(conditions_path.read_bytes()).hexdigest() != sha256:  # the hash must name the file built from
        raise ValueError(f"{conditions_path.name} changed while the bank was built")
    index, data = build_arrays(config, mapping, laser, ref_stereotaxic, ref_sample, blanking, sample_rate,
                               ceiling_mw, powers_mw)
    stamp = stamp or _dt.datetime.now().strftime("%Y_%m_%d__%H-%M-%S")
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    bin_path = out_dir / f"Settings_ZapitBank_{stamp}.bin"
    json_path = out_dir / f"Settings_ZapitBank_{stamp}.json"
    index = {"conditions_name": conditions_path.name,
             "conditions_sha256": sha256,
             "bin": bin_path.name, **index}
    bin_path.write_bytes(data)
    json_path.write_text(json.dumps(index, indent=1), encoding="utf-8")
    return json_path, bin_path


class BankData(NamedTuple):
    scan: np.ndarray    # (scan_rows, 3) galvo X, galvo Y, LED
    laser: np.ndarray   # (laser_rows,) control volts


def split_bank(index: dict, raw: bytes) -> BankData:
    flat = np.frombuffer(raw, dtype=DTYPE)
    n = index["scan_rows"] * SCAN_CHANNELS
    if flat.size != n + index["laser_rows"]:
        raise ValueError("The bank's .bin does not match its index")
    return BankData(flat[:n].reshape(-1, SCAN_CHANNELS), flat[n:])


def read_bank(json_path) -> tuple[dict, BankData]:
    """The index and the bin's two sections, the bin checked against its hash."""
    json_path = Path(json_path)
    index = json.loads(json_path.read_text(encoding="utf-8"))
    raw = json_path.with_name(index["bin"]).read_bytes()
    if hashlib.sha256(raw).hexdigest() != index["hashes"]["bin"]:
        raise ValueError(f"{index['bin']} does not match the hash in {json_path.name}")
    return index, split_bank(index, raw)


def find_variant(index: dict, condition: int, laser_on: bool, power_mw: float = 0.0) -> dict:
    for v in index["conditions"][condition - 1]["variants"]:
        if v["laser_on"] == laser_on and (not laser_on or v["power_mw"] == float(power_mw)):
            return v
    raise KeyError(f"The bank has no condition {condition} with laser_on={laser_on} at {power_mw} mW")


def assemble(index: dict, data: BankData, condition: int, laser_on: bool, power_mw: float, n_cycles: int):
    """A finite trial, (rows, 4) galvo X, galvo Y, laser, LED, from the bank: the scan cycle (n_cycles + tail_cycles)
    times and the laser cycle n_cycles times, each cut to the body's length (n_cycles x cycle - tail_replaces),
    then the laser tail; the final row's scan is ``scan_end``. Only selects and concatenates, as the node does."""
    cond = index["conditions"][condition - 1]
    v = find_variant(index, condition, laser_on, power_mw)
    sc, lc, lt, e = cond["scan_cycle"], v["laser_cycle"], v["laser_tail"], cond["scan_end"]
    n_body = n_cycles * sc["length"] - v["tail_replaces"]
    tail_rows = v["tail_cycles"] * sc["length"] or 1
    scan = np.tile(data.scan[sc["offset"]:sc["offset"] + sc["length"]], (n_cycles + v["tail_cycles"], 1))
    scan = scan[:n_body + tail_rows].copy()
    scan[-1] = data.scan[e["offset"]]
    laser = np.zeros(n_body + tail_rows, dtype=DTYPE)       # a null laser block is zeros: the buffer starts at 0
    if lc:
        laser[:n_body] = np.tile(data.laser[lc["offset"]:lc["offset"] + lc["length"]], n_cycles)[:n_body]
    if lt:
        laser[n_body:] = data.laser[lt["offset"]:lt["offset"] + lt["length"]]
    return np.c_[scan[:, 0], scan[:, 1], laser, scan[:, 2]]
