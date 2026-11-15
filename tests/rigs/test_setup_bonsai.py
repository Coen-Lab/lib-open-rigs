"""setup-bonsai.ps1 removes package folders Bonsai.config no longer names, after a good restore only."""

import shutil
import subprocess
import sys
from contextlib import nullcontext
from pathlib import Path

import pytest

SCRIPT = Path(__file__).parents[2] / "src" / "open_rigs" / "scripts" / "setup-bonsai.ps1"

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="Bonsai setup runs on Windows")

CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<PackageConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Packages>
    <Package id="OpenRigs.Core" version="0.1.1-local.17" />
    <Package id="openal.redist" version="2.0.7" />
  </Packages>
</PackageConfiguration>
"""

FOLDERS = ["OpenRigs.Core.0.1.1-local.17", "OpenRigs.Core.0.1.1-local.8", "openal.redist.2.0.7.0",
           "Bonsai.FFmpeg.0.2.0-uniquepipe", "not-a-package"]


def run_setup(tmp_path: Path, restore_exit: int, locked: str = "") -> tuple[subprocess.CompletedProcess, set[str]]:
    env = tmp_path / ".bonsai"
    (env / "Packages").mkdir(parents=True)
    (env / "Bonsai.config").write_text(CONFIG, encoding="utf-8")
    for name in FOLDERS:
        (env / "Packages" / name).mkdir()
        (env / "Packages" / name / "x.dll").write_text("x")
    # A stand-in Bonsai.exe: cmd.exe ends at once with no input, with exit 0; where.exe without a match exits 1.
    stand_in = "cmd.exe" if restore_exit == 0 else "where.exe"
    shutil.copy(Path(r"C:\Windows\System32") / stand_in, env / "Bonsai.exe")
    # A file held open in a folder, as a running Bonsai holds its assemblies: Windows refuses to delete it.
    with open(env / "Packages" / locked / "x.dll") if locked else nullcontext():
        run = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT),
                              "-Environment", str(env)], stdin=subprocess.DEVNULL, capture_output=True, text=True)
    return run, {p.name for p in (env / "Packages").iterdir()}


def test_a_good_restore_removes_the_folders_bonsai_config_no_longer_names(tmp_path):
    run, left = run_setup(tmp_path, 0)
    assert run.returncode == 0, run.stdout + run.stderr
    assert left == {"OpenRigs.Core.0.1.1-local.17", "openal.redist.2.0.7.0", "not-a-package"}
    assert "Removed OpenRigs.Core.0.1.1-local.8" in run.stdout


def test_a_folder_that_cannot_be_removed_is_said_and_does_not_fail_the_setup(tmp_path):
    run, left = run_setup(tmp_path, 0, locked="OpenRigs.Core.0.1.1-local.8")
    assert run.returncode == 0, run.stdout + run.stderr
    assert "Could not remove OpenRigs.Core.0.1.1-local.8" in run.stderr
    assert "Bonsai.FFmpeg.0.2.0-uniquepipe" not in left


def test_a_failed_restore_removes_nothing(tmp_path):
    run, left = run_setup(tmp_path, 1)
    assert run.returncode != 0
    assert left == set(FOLDERS)
