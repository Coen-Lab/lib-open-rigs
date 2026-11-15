import shutil
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).parents[2] / "src" / "open_rigs" / "scripts" / "check-workflow.ps1"
CONFIG = ('<?xml version="1.0" encoding="utf-8"?><PackageConfiguration><Packages>'
          '<Package id="Bonsai.Core" version="2.9.0" /><Package id="OpenRigs.Core" version="1.0.4" />'
          '</Packages></PackageConfiguration>')


@pytest.mark.skipif(shutil.which("powershell") is None, reason="Windows PowerShell runs the check")
def test_a_package_bonsai_config_names_but_packages_lacks_stops_it_naming_setup(tmp_path):
    # A stale .bonsai\Packages once failed only as "proxy for the unknown type ..." deep in the build.
    (tmp_path / ".bonsai" / "Packages" / "Bonsai.Core.2.9.0").mkdir(parents=True)
    (tmp_path / ".bonsai" / "Packages" / "OpenRigs.Core.1.0.3").mkdir()
    (tmp_path / ".bonsai" / "Bonsai.config").write_text(CONFIG, encoding="utf-8")
    run = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT), "-Repo", str(tmp_path)],
                         capture_output=True, text=True, timeout=120)
    assert run.returncode != 0
    said = "".join((run.stdout + run.stderr).split())  # PowerShell wraps a long error anywhere
    assert "".join(r"lacks what Bonsai.config names: OpenRigs.Core 1.0.4. Run .bonsai\Setup.ps1.".split()) in said
