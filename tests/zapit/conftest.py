import os
from pathlib import Path

for _var in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ.setdefault(_var, "1")

import pytest  # noqa: E402
from importlib.util import find_spec  # noqa: E402

HERE = Path(__file__).parent
DATA = HERE / "data"

# A plain ``uv sync`` has no ``zapit`` extra: collect only the layering test, which needs neither scipy nor cv2.
if any(find_spec(m) is None for m in ("scipy", "cv2")):
    collect_ignore = [p.name for p in HERE.glob("test_zapit_*.py") if p.name != "test_zapit_layering.py"]


@pytest.fixture
def data_dir() -> Path:
    return DATA
