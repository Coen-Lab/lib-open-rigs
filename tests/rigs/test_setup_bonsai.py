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


def test_a_different_layout_is_kept_as_main_saved_layout(tmp_path):
    (tmp_path / "config").mkdir()
    (tmp_path / "config" / "main.layout").write_text("tracked", encoding="utf-8")
    settings = tmp_path / ".bonsai" / "Settings" / "src"
    settings.mkdir(parents=True)
    (settings / "main.layout").write_text("the person's", encoding="utf-8")
    run, _ = run_setup(tmp_path, 0)
    assert run.returncode == 0, run.stdout + run.stderr
    assert (settings / "main.saved.layout").read_text(encoding="utf-8") == "the person's"
    assert (settings / "main.layout").read_text(encoding="utf-8") == "tracked"
    assert "Get-FileHash" not in SCRIPT.read_text(encoding="utf-8")


def test_a_failed_restore_removes_nothing(tmp_path):
    run, left = run_setup(tmp_path, 1)
    assert run.returncode != 0
    assert left == set(FOLDERS)


# A stand-in restore: Bonsai.config is first replaced by next.config when there is one (kept when `always` is there, as
# a restore that drops a package every time), then a folder is made for each package it names.
RESTORE = r"""using System.IO; using System.Text.RegularExpressions;
class Restore { static int Main() {
    if (File.Exists("next.config")) { File.Copy("next.config", "Bonsai.config", true); if (!File.Exists("always")) File.Delete("next.config"); }
    foreach (Match m in Regex.Matches(File.ReadAllText("Bonsai.config"), "id=\"([^\"]+)\" version=\"([^\"]+)\""))
        Directory.CreateDirectory(Path.Combine("Packages", m.Groups[1].Value + "." + m.Groups[2].Value));
    return 0; } }
"""
CSC = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe")
DAQMX = '    <Package id="Bonsai.DAQmx" version="0.5.0" />\n'


@pytest.fixture(scope="module")
def restore_exe(tmp_path_factory):
    if not CSC.exists():
        pytest.skip("no .NET Framework C# compiler to build the stand-in restore")
    folder = tmp_path_factory.mktemp("restore")
    (folder / "Restore.cs").write_text(RESTORE, encoding="utf-8")
    subprocess.run([str(CSC), "/nologo", f"/out:{folder / 'Bonsai.exe'}", str(folder / "Restore.cs")], check=True,
                   capture_output=True)
    return folder / "Bonsai.exe"


def run_dropping_restore(tmp_path: Path, exe: Path, always: bool, adds: bool = False) -> tuple[subprocess.CompletedProcess, Path]:
    env = tmp_path / ".bonsai"
    (env / "Packages").mkdir(parents=True)
    with_daqmx = CONFIG.replace("  </Packages>", DAQMX + "  </Packages>")
    # The update's rewrite: without Bonsai.DAQmx, or (adds) with it beside the committed file's packages.
    committed, rewrite = (CONFIG, with_daqmx) if adds else (with_daqmx, CONFIG)
    (env / "Bonsai.config").write_text(committed, encoding="utf-8")
    (env / "next.config").write_text(rewrite, encoding="utf-8")
    if always:
        (env / "always").write_text("")
    shutil.copy(exe, env / "Bonsai.exe")
    run = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT),
                          "-Environment", str(env)], stdin=subprocess.DEVNULL, capture_output=True, text=True)
    # The committed file, whatever happened; a rewrite that only adds is kept.
    assert (env / "Bonsai.config").read_text(encoding="utf-8") == (rewrite if adds else committed)
    return run, env / "Packages"


def test_a_restore_that_drops_a_package_is_put_back_and_restored_again(tmp_path, restore_exe):
    run, packages = run_dropping_restore(tmp_path, restore_exe, always=False)
    assert run.returncode == 0, run.stdout + run.stderr
    assert "putting it back and restoring again" in run.stdout
    assert (packages / "Bonsai.DAQmx.0.5.0").is_dir()


def test_a_package_still_missing_after_the_second_restore_fails_the_setup_naming_it(tmp_path, restore_exe):
    run, packages = run_dropping_restore(tmp_path, restore_exe, always=True)
    assert run.returncode == 1
    assert "Bonsai.config names packages the restore did not install: Bonsai.DAQmx 0.5.0" in run.stderr
    assert not (packages / "Bonsai.DAQmx.0.5.0").exists()


def test_a_package_the_restore_adds_to_bonsai_config_keeps_its_folder(tmp_path, restore_exe):
    run, packages = run_dropping_restore(tmp_path, restore_exe, always=False, adds=True)
    assert run.returncode == 0, run.stdout + run.stderr
    assert (packages / "Bonsai.DAQmx.0.5.0").is_dir()  # Bonsai.config names it: removing it broke the workflow
