"""lib-open-rigs holds nothing lab-only: no lab app, path or person in its files.

The files listed below already named the lab when the rule began (v8), each with the number of its
lines that do; cleaning them is separate work, and each count falls, and the file leaves the list, as it
is cleaned. A listed file may not gain a line. Every other tracked text file names the lab on none.
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
    "README.md": 1,
    "build/Common.csproj.targets": 2,
    "docs/bonsai-operators/OpenRigs.Logging/log-async.md": 1,
    "docs/bonsai-operators/OpenRigs.Logging/log-trial.md": 3,
    "fixtures/manifest/v3/completed.json": 10,
    "fixtures/manifest/v3/ended-early.json": 10,
    "fixtures/manifest/v3/no-controller.json": 9,
    "fixtures/manifest/v3/refuse/no-version.json": 10,
    "fixtures/manifest/v3/refuse/unknown-version.json": 10,
    "fixtures/manifest/v3/refuse/version-2.json": 10,
    "fixtures/manifest/v3/running.json": 9,
    "fixtures/manifest/v3/stopped-not-final.json": 10,
    "patches/README.md": 16,
    "src/OpenRigs.Control.Tests/Program.cs": 3,
    "src/OpenRigs.Control/ControlServer.cs": 3,
    "src/OpenRigs.Control/ExperimentOperators.cs": 1,
    "src/OpenRigs.Control/OpenRigs.Control.csproj": 1,
    "src/OpenRigs.Control/README.md": 8,
    "src/OpenRigs.Core/OpenRigs.Core.csproj": 1,
    "src/OpenRigs.Devices/OpenRigs.Devices.csproj": 1,
    "src/OpenRigs.HarpDevices.Tests/Program.cs": 2,
    "src/OpenRigs.HarpDevices/OpenRigs.HarpDevices.csproj": 1,
    "src/OpenRigs.HarpDevices/README.md": 7,
    "src/OpenRigs.HarpDevices/UsbSerials.cs": 3,
    "src/OpenRigs.LiveView.Tests/Program.cs": 3,
    "src/OpenRigs.LiveView/LiveViewPlan.cs": 2,
    "src/OpenRigs.LiveView/OpenRigs.LiveView.csproj": 1,
    "src/OpenRigs.LiveView/README.md": 8,
    "src/OpenRigs.LiveView/Templates/StreamController.xml": 1,
    "src/OpenRigs.Logging/CsvWriter.cs": 1,
    "src/OpenRigs.Logging/OpenRigs.Logging.csproj": 1,
    "src/OpenRigs.Manifest.Tests/Program.cs": 26,
    "src/OpenRigs.Manifest/ExpectedFiles.cs": 1,
    "src/OpenRigs.Manifest/FORMAT.md": 3,
    "src/OpenRigs.Manifest/ManifestRecorder.cs": 3,
    "src/OpenRigs.Manifest/ManifestWriter.cs": 4,
    "src/OpenRigs.Manifest/OpenRigs.Manifest.csproj": 1,
    "src/OpenRigs.Manifest/README.md": 9,
    "src/OpenRigs.Streaming/OpenRigs.Streaming.csproj": 1,
    "src/OpenRigs.Video/OpenRigs.Video.csproj": 1,
    "src/OpenRigs.Video/TriggeredSpinnaker.cs": 1,
    "src/OpenRigs.Video/UsbCamera.cs": 3,
    "src/OpenRigs.Vision/OpenRigs.Vision.csproj": 1,  # the lab's package version, as every lab package's
    "src/Shared/GroupNode.cs": 1,
    "src/Shared/Json.cs": 1,
    "src/Shared/RigDevices.cs": 3,
    "src/open_rigs/rigs/behaviour_box.py": 7,
    "src/open_rigs/trials.py": 1,
}


def lab_text(text: str) -> list[str]:
    return [f"{number}: {match.group(0)}" for number, line in enumerate(text.splitlines(), 1) if (match := LAB_ONLY.search(line))]


def test_no_file_names_the_lab_on_more_lines_than_listed():
    names = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.splitlines()
    found = []
    for name in names:
        if name == THIS_FILE or not (ROOT / name).is_file():
            continue
        data = (ROOT / name).read_bytes()
        if b"\0" in data:
            continue  # binary
        hits = lab_text(data.decode("utf-8", errors="replace"))
        allowed = NAMED_THE_LAB_BEFORE_THE_RULE.get(name, 0)
        if len(hits) != allowed:
            found.append(f"{name}: {len(hits)} lab line(s), {allowed} listed:\n  " + "\n  ".join(hits))
    assert not found, "lab-only text in lib-open-rigs (a lower count is cleaning: lower the listed number):\n" + "\n".join(found)


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
