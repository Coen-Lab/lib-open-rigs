"""lib-open-rigs holds nothing lab-only: no lab app, path or person in its files.

The files listed below already named the lab when the rule began (v8); cleaning them is separate
work and each leaves the list as it is cleaned. Every other tracked text file is checked.
"""

import re
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).parents[1]
THIS_FILE = "tests/test_lab_free.py"

LAB_ONLY = re.compile(
    r"(?i:coen|app-rig-control|app-rig-setup|rig-control|rig-setup|K:\\|C:\\Github)"
    r"|\b(?:Pip|Tom|Gerion|Nabbefeld|Childs|Ainiah|Masood|T[uü]rker|Jenkin|jkin2)\b"
)

NAMED_THE_LAB_BEFORE_THE_RULE = {
    "README.md",
    "build/Common.csproj.targets",
    "docs/bonsai-operators/OpenRigs.Logging/log-async.md",
    "docs/bonsai-operators/OpenRigs.Logging/log-trial.md",
    "fixtures/manifest/v3/completed.json",
    "fixtures/manifest/v3/ended-early.json",
    "fixtures/manifest/v3/no-controller.json",
    "fixtures/manifest/v3/refuse/no-version.json",
    "fixtures/manifest/v3/refuse/unknown-version.json",
    "fixtures/manifest/v3/refuse/version-2.json",
    "fixtures/manifest/v3/running.json",
    "fixtures/manifest/v3/stopped-not-final.json",
    "patches/README.md",
    "src/OpenRigs.Control.Tests/Program.cs",
    "src/OpenRigs.Control/ControlServer.cs",
    "src/OpenRigs.Control/ExperimentOperators.cs",
    "src/OpenRigs.Control/OpenRigs.Control.csproj",
    "src/OpenRigs.Control/README.md",
    "src/OpenRigs.Core/OpenRigs.Core.csproj",
    "src/OpenRigs.Devices/OpenRigs.Devices.csproj",
    "src/OpenRigs.HarpDevices.Tests/Program.cs",
    "src/OpenRigs.HarpDevices/HarpDevicePlan.cs",
    "src/OpenRigs.HarpDevices/OpenRigs.HarpDevices.csproj",
    "src/OpenRigs.HarpDevices/README.md",
    "src/OpenRigs.HarpDevices/UsbSerials.cs",
    "src/OpenRigs.HarpDevices/WiringCheck.cs",
    "src/OpenRigs.LiveView.Tests/Program.cs",
    "src/OpenRigs.LiveView/LiveViewPlan.cs",
    "src/OpenRigs.LiveView/OpenRigs.LiveView.csproj",
    "src/OpenRigs.LiveView/README.md",
    "src/OpenRigs.LiveView/Templates/StreamController.xml",
    "src/OpenRigs.Logging/CsvWriter.cs",
    "src/OpenRigs.Logging/OpenRigs.Logging.csproj",
    "src/OpenRigs.Manifest.Tests/Program.cs",
    "src/OpenRigs.Manifest/ExpectedFiles.cs",
    "src/OpenRigs.Manifest/FORMAT.md",
    "src/OpenRigs.Manifest/ManifestRecorder.cs",
    "src/OpenRigs.Manifest/ManifestWriter.cs",
    "src/OpenRigs.Manifest/OpenRigs.Manifest.csproj",
    "src/OpenRigs.Manifest/README.md",
    "src/OpenRigs.Streaming/OpenRigs.Streaming.csproj",
    "src/OpenRigs.Video/OpenRigs.Video.csproj",
    "src/OpenRigs.Video/TriggeredSpinnaker.cs",
    "src/OpenRigs.Video/UsbCamera.cs",
    "src/Shared/GroupNode.cs",
    "src/Shared/Json.cs",
    "src/Shared/RigDevices.cs",
    "src/open_rigs/rigs/behaviour_box.py",
    "src/open_rigs/trials.py",
}


def lab_text(text: str) -> list[str]:
    return [f"{number}: {match.group(0)}" for number, line in enumerate(text.splitlines(), 1) if (match := LAB_ONLY.search(line))]


def test_no_other_file_names_the_lab():
    names = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.splitlines()
    found = []
    for name in names:
        if name in NAMED_THE_LAB_BEFORE_THE_RULE or name == THIS_FILE or not (ROOT / name).is_file():
            continue
        data = (ROOT / name).read_bytes()
        if b"\0" in data:
            continue  # binary
        found += [f"{name}:{hit}" for hit in lab_text(data.decode("utf-8", errors="replace"))]
    assert not found, "lab-only text in lib-open-rigs:\n" + "\n".join(found)


def test_listed_files_are_still_there():
    missing = [name for name in NAMED_THE_LAB_BEFORE_THE_RULE if not (ROOT / name).is_file()]
    assert not missing, f"take these off the list: {missing}"


@pytest.mark.parametrize(
    "line", [r"K:\config\machines", r"C:\Github\lib", "see app-rig-control", "Coen Lab", "asked Gerion", "Tom's rig"]
)
def test_the_pattern_catches_lab_text(line):
    assert lab_text(line)


@pytest.mark.parametrize("line", ["pip install", "Tomorrow", "atom", "a rig's config"])
def test_the_pattern_passes_ordinary_text(line):
    assert not lab_text(line)
