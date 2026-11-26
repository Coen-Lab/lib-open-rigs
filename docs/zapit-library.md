# open_rigs.zapit

The Zapit maths without hardware, Qt or sockets: transforms, scanner and laser fits, waveforms, conditions files, the atlas, the bank builder, per-session scanner calibration, the sample (clicks and registration) and the rig.yml `zapit` reader.

It is the `zapit` extra of this package: `uv sync --extra zapit` (numpy, scipy, opencv-python). The core installs without it, and nothing outside `src/open_rigs/zapit/` imports it (`tests/zapit/test_zapit_layering.py`). It moved here from the Zapit app's `lib_zapit/` folder at commit 9dbb1af of that repo (version 0.1.0 there); its import name was `lib_zapit`.

Tests: `uv run pytest tests/zapit -n auto`.

## Modules

| Module | Public functions |
| --- | --- |
| `rig_config` | `read_rig_zapit(rig_yml, require=True) -> ZapitRig \| None`, `ZapitRig.from_dict(block)`, `ZapitRig.laser(fit=None)` |
| `safety` | `max_control_volts(laser, ceiling_mw)`, `laser_volts(laser, mw, ceiling_mw)` |
| `scanner` | `with_found_area(settings, still)` and `with_rectangle_area(settings, (u0, v0, u1, v1))` -> (settings with the grid kept on the target less the buffer, or None and why), `grid_points_mm(settings)`, `calibration_plan(settings, laser, ceiling_mw) -> ScanPlan` (at `calibration_power_mw`, 2 mW by default, capped by `ceiling_mw`), `fit_scanner(plan, frames, backgrounds, settings) -> ScannerFit`, `fit_scanner_run(plan, folder, settings)`, `accuracy_plan(settings, fit, laser, ceiling_mw) -> ScanPlan`, `measure_accuracy(plan, frames, backgrounds, settings) -> Accuracy`, `measure_accuracy_run(plan, folder, settings)`, `load_frames(folder)`, `save_frames(folder, points, backgrounds)`, `ScannerSettings.from_rig(rig, image_size, roi_offset_mm, **grid)`, `ScannerFit.to_dict()` (the session's record: strict JSON, with its `mapping` and `scanner_fit_hash`), `mapping_from_record(record)` (the mapping back from that record alone) |
| `laser_curve` | `staircase_volts(min_max_control_volts, points=201, *, test_subject) -> ndarray`, `fit_laser_curve(volts, samples) -> LaserFit` (raises `LaserFitRefused`) |
| `sample` | `sample_from_clicks(bregma_px, lambda_px, settings) -> Sample`, `register_sample(reference_image, reference_sample, image, settings) -> Registration`, `skull_mask(img)`; constants `MIN_INLIERS = 30`, `MIN_CORRELATION = 0.6`, `MAX_ROTATION_DEG = 20` (and the other tunables) at the top of `sample.py` |
| `bank` | `build_bank(conditions_path, out_dir, mapping, laser, ref_stereotaxic, ref_sample, blanking, sample_rate, ceiling_mw, powers_mw=None, stamp=None) -> (json_path, bin_path)`, `read_bank(json_path) -> (index, BankData)`, `assemble(index, data, condition, laser_on, power_mw, n_cycles)` (the reference for the node), `find_variant` |

`ceilingMw` caps every volts value handed out (bank, calibration and accuracy grids). **`ceilingMw: 0` means the laser is disabled**: every laser volts value is 0 V (bank laser blocks null, calibration and accuracy grids 0 V), and `staircase_volts(..., ceiling_mw=0)` raises `PermissionError`. The laser-curve staircase alone bypasses a non-zero ceiling, and `staircase_volts` raises `PermissionError` unless `test_subject=True`.

## rig.yml `zapit` block (the reader's fields)

Required: `device`, `galvoXChannel`, `galvoYChannel`, `laserChannel`, `ledChannel` (AO lines), `photodiodeChannel` (AI), `triggerChannel` (PFI), `sampleRate`, `laserMinMaxMw` (rising pair), `laserMinMaxControlVolts` (rising pair), `ceilingMw` (0 or above; 0 = laser disabled), `maxTrialSeconds`, `micronsPerPixel`, `voltsPerPixel`, `camera: {serialNumber, sessionPfsPath?, calibrationPfsPath?}` (paths relative to rig.yml's folder, defaults `calibration/Zapit-Session.pfs` and `calibration/Zapit-Calibration.pfs`). Optional: `invertXScanner`, `invertYScanner` (default false). Unknown keys (including the retired `safetyGate`, and `earlyStopLeadSeconds` and `triggerFilterSeconds`, now lib-open-rigs constants), a missing field and a bad value are refused with the field named. No block is `None` with `require=False` (a desk, a rig with no Zapit).

## Frame folders (scanner calibration and Measure Accuracy)

One folder per run (`runs\zapit-scanner\<run>\` for the fit, any folder for accuracy), lossless grey PNG, 8 or 16 bit, the ROI image as the camera delivers it:

- `point_<index>.png`: the frame saved after moving to plan point `index` (0-based, into `ScanPlan.points_mm`; zero padding free, the writer uses 4 digits). A missing index counts as a missed point.
- `background_<k>.png`: laser-off frames, `k` = 0..3 (4 frames; the background is their per-pixel maximum, and their flicker sets the dimmest spot accepted).

The workflow's steps: write `ScanPlan.volts[i]` to the galvos, laser at `ScanPlan.laser_volts` (laser off for the backgrounds), skip 3 frames (`SKIP_FRAMES`), save one frame. The first background set is taken with the beam at point 0, laser off.

## Bank files

`Settings_ZapitBank_<DateTime>.json` (index) and `.bin`. The bin is float32 little-endian: a **scan section** of `scan_rows` x 3 (galvo X, galvo Y, LED, interleaved) followed by a **laser section** of `laser_rows` x 1 (control volts). Offsets and lengths are in rows of their own section. Identical blocks are stored once, so a real task's bank (8 conditions of 1 or 2 points, one power, a 0.5 s ramp, 40 Hz, 100 kS/s) is about 0.65 MB; the laser ramp tail is one column and shared by conditions alike in points, ramp and ephys.

Index fields: `version` (2), `lib_zapit` (the code that built it: `<version>+g<commit>`, `-dirty` for local edits, as the version never moves; the key keeps its old name), `conditions_name`, `conditions_sha256`, `bin` (file name), `sample_rate`, `modulation_hz`, `ceiling_mw`, `blanking_times_ms` (blanking time, onset and offset shift, as used), `scan_channels`, `dtype`, `layout`, `scan_rows`, `laser_rows`, `hashes: {scanner_fit, laser_curve, sample, bin}` (the `bin` hash is the sha256 of the file), and `conditions[]`:

- `condition` (1-based), `points_mm` (the workflow's draw points, sample mm), `stereotaxic_mm`, `n_points`
- `scan_cycle {offset, length}`: one modulation cycle, scan section (L = length rows)
- `scan_end {offset, length: 1}`: the trial's final scan row (last cycle row's galvos, LED 0)
- `variants[]`: `laser_on`, `power_mw` (0 for laser off), `capped_power_mw`, `power_capped`, `laser_cycle {offset, length: L} | null`, `laser_tail {offset, length} | null`, `tail_cycles` (ramp length in cycles; 0 = no ramp), `tail_replaces` (0, or 1 when there is no ramp)

A `null` laser block means zeros. A trial of N cycles has `n_body = N*L - tail_replaces` body rows and `tail_rows = tail_cycles*L` (1 when `tail_cycles` is 0). Build the buffer, 4 channels: scan X, Y, LED = `scan_cycle` tiled `N + tail_cycles` times, cut to `n_body + tail_rows`, final row replaced by `scan_end`; laser = `laser_cycle` tiled N times cut to `n_body`, then `laser_tail` (zeros where null). The node only selects, tiles and concatenates; `bank.assemble` is the same in Python and `tests/zapit/test_zapit_bank.py` holds it equal to the app's `Zapit.trial_waveforms`.

## Bank records and `source()`

The index's `lib_zapit` value names a Zapit-app commit for a bank built before a consumer moved to this package, and a commit of this repository after. Readers check presence only. `source()` reads the commit from the installed distribution's `direct_url.json` (PEP 610) when the package was installed from git, and from `git rev-parse` in an editable or source checkout.

## The contract fixture

`tests/zapit/data/contract_expected.npz` is a frozen record: it was made at commit 9dbb1af of the Zapit app by its `make_contract_fixture.py`, which needed that app's `Zapit` class and was not moved. A change to how a trial is built regenerates it from `bank.assemble` itself, deliberately. `src/OpenRigs.Zapit.Tests/Data/make_fixture.py` turns it into the C# tests' fixture: `uv run python src/OpenRigs.Zapit.Tests/Data/make_fixture.py tests/zapit`.

## Licence

`open_rigs.zapit` is LGPL-2.1-only, upstream's terms (`LICENSE` in this folder), not this repository's BSD-3-Clause. Its transforms, waveforms and calibration scheme are ports of [Zapit-Optostim/zapit](https://github.com/Zapit-Optostim/zapit) by Maja Skretowska and Rob Campbell (SWC), whose `LICENSE` is LGPL-2.1 and whose source headers grant no "or later". Decided by the repository maintainer, 26 Nov 2026: keep upstream's terms rather than relicense, as only upstream's authors can grant more.
