"""lib-open-rigs holds nothing lab-only: no lab app, path or person in its files.

The files listed below name the lab on purpose, each with the number of its lines that do: the fork's own notice,
each package's lab version (0.1.1-coen-lab.N) and the build rule that keeps it, and the version 3 manifest fixtures,
real records that readers still test on. A listed file may not gain a line. Every other tracked text file names the
lab on none.
"""

import re
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).parents[1]
THIS_FILE = "tests/test_lab_free.py"

LAB_ONLY = re.compile(
    r"(?i:coen|jacuzzi|app-rig-control|app-rig-setup|rig[- ]control\b|rig[- ]setup\b|K:\\|C:\\Github)"
    r"|\b(?:Pip|Tom|Gerion|Nabbefeld|Childs|Ainiah|Masood|T[uü]rker|Jenkin|jkin2)\b"
    r"|\b[A-Z]+-(?:OPTO|DOME|BBOX)\b|\bBBOX-?\d|(?i:\bdomes?\b)"
)

NAMES_THE_LAB = {
    ".github/workflows/records.yml": 3,  # the lab's nightly gate, Coen-Lab/.github ci-gate.yml
    "README.md": 1,
    "build/Common.csproj.targets": 2,
    "fixtures/manifest/v3/completed.json": 10,
    "fixtures/manifest/v3/ended-early.json": 10,
    "fixtures/manifest/v3/no-controller.json": 9,
    "fixtures/manifest/v3/refuse/no-version.json": 10,
    "fixtures/manifest/v3/refuse/unknown-version.json": 10,
    "fixtures/manifest/v3/refuse/version-2.json": 10,
    "fixtures/manifest/v3/running.json": 9,
    "fixtures/manifest/v3/stopped-not-final.json": 10,
    "fixtures/rig-yml/B1.yml": 7,  # four rigs' rig.yml as they are on the share, byte for byte: real records
    "fixtures/rig-yml/B2-both.yml": 6,  # B2 with ZapitDaq's fields beside MATLAB Zapit's
    "fixtures/rig-yml/B2.yml": 5,
    "fixtures/rig-yml/G1.yml": 10,
    "fixtures/rig-yml/G2.yml": 8,
    "fixtures/lab-settings/v1/as-on-k.yml": 1,  # the share's lab.yml as it is, copied byte for byte from its other reader
    "fixtures/lab-settings/v1/extra-key.yml": 1,  # the same set's refused extra key, owner: <a lab member>
    "patches/README.md": 1,
    "src/OpenRigs.Control/OpenRigs.Control.csproj": 1,
    "src/OpenRigs.Audio/OpenRigs.Audio.csproj": 1,
    "src/OpenRigs.HarpDevices/OpenRigs.HarpDevices.csproj": 1,
    "src/OpenRigs.Manifest/OpenRigs.Manifest.csproj": 1,
    "src/OpenRigs.Streaming/OpenRigs.Streaming.csproj": 1,
    "src/OpenRigs.Video/OpenRigs.Video.csproj": 1,
    "src/open_rigs/rigs/workflow_checks.py": 1,  # the lab's ConfigRoot, which every task's check holds it to
    "tests/rigs/test_workflow_checks.py": 1,
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
        allowed = NAMES_THE_LAB.get(name, 0)
        if len(hits) != allowed:
            found.append(f"{name}: {len(hits)} lab line(s), {allowed} listed:\n  " + "\n  ".join(hits))
    assert not found, "lab-only text in lib-open-rigs (a lower count is cleaning: lower the listed number):\n" + "\n".join(found)


def test_listed_files_are_still_there():
    missing = [name for name in NAMES_THE_LAB if not (ROOT / name).is_file()]
    assert not missing, f"take these off the list: {missing}"


@pytest.mark.parametrize(
    "line",
    [
        r"K:\config\machines", r"C:\Github\lib", "see app-rig-control", "as Rig Control shows", "in Rig Setup", "Coen Lab",
        "asked Gerion", "Tom's rig", "on BLUE-OPTO-2", "GREEN-DOME-1's camera", "GEEK-BBOX1", "BBOX-1's board", "every dome",
        "the Dome task", "both domes", "av-dome's", "the overnight copy (jacuzzi-ingest)",
    ],
)
def test_the_pattern_catches_lab_text(line):
    assert lab_text(line)


@pytest.mark.parametrize(
    "line", ["pip install", "Tomorrow", "atom", "a rig's config", "the value the rig controls", "OPTO = {", "domestic", "0.1.0-bbox.5"]
)
def test_the_pattern_passes_ordinary_text(line):
    assert not lab_text(line)
