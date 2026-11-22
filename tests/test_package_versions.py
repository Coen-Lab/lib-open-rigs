"""Package versions live in src/Directory.Packages.props only: no csproj under src pins one."""

import re
from pathlib import Path

SRC = Path(__file__).parents[1] / "src"
REFERENCE = re.compile(r"<PackageReference\b[^>]*>")


def test_no_csproj_pins_a_package_version():
    pinned = [
        f"{path.relative_to(SRC)}: {tag}"
        for path in SRC.glob("*/*.csproj")
        for tag in REFERENCE.findall(path.read_text(encoding="utf-8-sig"))
        if re.search(r"\bVersion(Override)?\s*=", tag)
    ]
    assert not pinned, f"Move these versions to src/Directory.Packages.props: {pinned}"


def test_every_referenced_package_has_one_central_version():
    props = (SRC / "Directory.Packages.props").read_text(encoding="utf-8-sig")
    central = re.findall(r'<PackageVersion Include="([^"]+)"', props)
    assert len(central) == len(set(central)), "A package is listed twice in Directory.Packages.props"
    used = {
        name
        for path in SRC.glob("*/*.csproj")
        for name in re.findall(r'<PackageReference Include="([^"]+)"', path.read_text(encoding="utf-8-sig"))
    }
    assert used == set(central)
