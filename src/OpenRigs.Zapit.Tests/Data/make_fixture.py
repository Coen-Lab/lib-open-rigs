"""Regenerates this folder's bank fixture from the contract fixture in tests/zapit/data.

Not run by CI. Run whenever open_rigs.zapit's bank layout changes, from the repository root:
    uv run python src/OpenRigs.Zapit.Tests/Data/make_fixture.py tests/zapit
Writes Settings_ZapitBank_T.json, Settings_ZapitBank_T.bin.gz, contract_conditions.yml and, for a few of the contract's
cases, the app's own trial waveforms (contract_expected.npz) as float32 in contract_expected.bin.gz with an index
contract_expected.json. Gzipped to keep the repo small; the tests unpack them.
"""
import gzip
import json
import shutil
import sys
from pathlib import Path

import numpy as np

LIB = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(LIB))
from test_zapit_bank import rig  # noqa: E402  the contract's rig, as the library's own test builds it

from open_rigs.zapit import bank  # noqa: E402

HERE = Path(__file__).parent
DATA = LIB / "data"
CASES = [(1, True, 8.0, 2), (2, True, 40.0, 4), (3, True, 2.0, 3), (3, False, 2.0, 3)]

inputs = json.loads((DATA / "contract_inputs.json").read_text())
json_path, bin_path = bank.build_bank(DATA / "contract_conditions.yml", HERE, stamp="T", **rig(inputs))
with gzip.open(HERE / (bin_path.name + ".gz"), "wb", compresslevel=9) as f:
    f.write(bin_path.read_bytes())
bin_path.unlink()
shutil.copy(DATA / "contract_conditions.yml", HERE)
cases, blocks = [], []
with np.load(DATA / "contract_expected.npz") as z:
    for c, on, p, n in CASES:
        want = z[f"c{c}_{'on' if on else 'off'}_{p:g}_{n}"].astype("<f4")
        cases.append({"condition": c, "laser_on": on, "power_mw": p, "cycles": n, "rows": int(want.shape[0])})
        blocks.append(want.tobytes())
with gzip.open(HERE / "contract_expected.bin.gz", "wb", compresslevel=9) as f:
    f.write(b"".join(blocks))
(HERE / "contract_expected.json").write_text(json.dumps({"columns": ["galvo_x", "galvo_y", "laser", "led"],
                                                         "cases": cases}, indent=1), encoding="utf-8")
print(json_path.name, [c["rows"] for c in cases])
