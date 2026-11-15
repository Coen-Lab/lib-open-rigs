import sys
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).parents[2] / "src" / "open_rigs" / "scripts" / "check-workflow.ps1"
POWERSHELL = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass"]
windows_only = pytest.mark.skipif(sys.platform != "win32", reason="the check runs under Windows PowerShell")


def environment(root: Path, packages: dict[str, str], folders: list[str]) -> Path:
    """A workflow repository's .bonsai: a Bonsai.config naming `packages` (id: version), and `folders` in Packages."""
    config = "".join(f'<Package id="{id}" version="{version}" />' for id, version in packages.items())
    (root / ".bonsai" / "Packages").mkdir(parents=True)
    (root / ".bonsai" / "Bonsai.config").write_text(
        f'<?xml version="1.0" encoding="utf-8"?><PackageConfiguration><Packages>{config}</Packages></PackageConfiguration>', encoding="utf-8")
    for folder in folders:
        (root / ".bonsai" / "Packages" / folder).mkdir(parents=True)
    return root


def check(repo: Path) -> subprocess.CompletedProcess:
    return subprocess.run(POWERSHELL + ["-File", str(SCRIPT), "-Repo", str(repo)], capture_output=True, text=True, timeout=300)


def lacks(id: str, version: str, found: str) -> str:
    return (f"check-workflow: .bonsai\\Packages lacks {id} {version}, the version Bonsai.config names (found: {found}). "
            "Run .bonsai\\Setup.ps1.")


# The lab packages' versions are 0.1.1-<lab>-lab.N, here for no lab.
@windows_only
@pytest.mark.parametrize("wanted, folder, found", [
    ("0.1.1-fork-lab.14", "OpenRigs.Core.0.1.1-fork-lab.14", None),
    ("0.1.1-fork-lab.14", "OpenRigs.Core.0.1.1-fork-lab.13", "0.1.1-fork-lab.13"),
    ("0.1.1-fork-lab.14", "OpenRigs.Core.0.1.1-fork-lab.140", "0.1.1-fork-lab.140"),
    ("0.1.1-fork-lab.14", "OpenRigs.Core.Extra.0.1.1-fork-lab.14", "none"),
    ("1.0", "OpenRigs.Core.1.0.0", None),
    ("1.0.0", "OpenRigs.Core.1.0", None),
    ("2.10", "OpenRigs.Core.2.1", "2.1"),
    ("2.1", "OpenRigs.Core.2.10", "2.10"),
])
def test_a_package_is_found_only_at_the_version_bonsai_config_names(tmp_path, wanted, folder, found):
    run = check(environment(tmp_path, {"OpenRigs.Core": wanted}, [folder]))
    if found is None:
        assert "lacks" not in run.stdout + run.stderr
        assert "1. Compiling src\\Extensions as C# 5..." in run.stdout
    else:
        assert run.returncode == 1
        assert run.stdout.splitlines() == [lacks("OpenRigs.Core", wanted, found)]


@windows_only
def test_with_every_package_present_the_check_goes_on_to_the_build(tmp_path):
    repo = environment(tmp_path, {"Bonsai.Core": "2.9.0", "openal.redist": "2.0.7", "OpenRigs.Core": "0.1.1-fork-lab.14"},
                       ["Bonsai.Core.2.9.0", "openal.redist.2.0.7.0", "OpenRigs.Core.0.1.1-fork-lab.14"])
    run = check(repo)
    assert "lacks" not in run.stdout + run.stderr
    assert "1. Compiling src\\Extensions as C# 5..." in run.stdout


@windows_only
def test_each_stale_package_is_one_line_naming_what_is_wanted_and_found_with_no_powershell_error(tmp_path):
    # A throw printed PowerShell's "At ... char" lines and never said which version was there.
    repo = environment(tmp_path, {"Bonsai.Core": "2.9.0", "OpenRigs.Core": "0.1.1-fork-lab.14", "OpenRigs.Audio": "0.1.1-fork-lab.8"},
                       ["Bonsai.Core.2.9.0", "OpenRigs.Core.0.1.1-fork-lab.9", "OpenRigs.Core.0.1.1-fork-lab.13"])
    run = check(repo)
    assert run.returncode == 1
    assert run.stdout.splitlines() == [lacks("OpenRigs.Core", "0.1.1-fork-lab.14", "0.1.1-fork-lab.13, 0.1.1-fork-lab.9"),
                                       lacks("OpenRigs.Audio", "0.1.1-fork-lab.8", "none")]
    assert run.stderr == ""


@windows_only
def test_the_resolver_takes_an_assembly_bonsai_config_does_not_locate_from_the_version_it_names(tmp_path):
    # Sorted by name, fork-lab.9 came before fork-lab.14, so a resolver over every Packages folder took 9.
    repo = environment(tmp_path, {"OpenRigs.Core": "0.1.1-fork-lab.14"},
                       ["OpenRigs.Core.0.1.1-fork-lab.9", "OpenRigs.Core.0.1.1-fork-lab.14"])
    libs = [repo / ".bonsai" / "Packages" / f"OpenRigs.Core.0.1.1-fork-lab.{version}" / "lib" / "net472" for version in ("9", "14")]
    command = (
        f"$ErrorActionPreference = 'Stop'; $bonsaiDir = '{repo / '.bonsai'}'; $bin = '{tmp_path / 'bin'}'; "
        + "".join(f"$null = New-Item -ItemType Directory '{lib}'; Add-Type -TypeDefinition 'public class Marker {{}}' "
                  f"-OutputAssembly '{lib / 'OpenRigs.Core.dll'}'; " for lib in libs)
        + f"$ast = [Management.Automation.Language.Parser]::ParseFile('{SCRIPT}', [ref]$null, [ref]$null); "
        "foreach ($f in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) "
        "{ . ([scriptblock]::Create($f.Extent.Text)) }; "
        # Initialize-Assemblies registers the resolver, then stops at loading Bonsai.Core, which this repository lacks.
        "try { Initialize-Assemblies } catch {}; "
        "$script:resolver.Invoke($null, [ResolveEventArgs]::new('OpenRigs.Core')).Location")
    run = subprocess.run(POWERSHELL + ["-Command", command], capture_output=True, text=True, timeout=120)
    assert run.stderr == ""
    assert run.stdout.strip() == str(libs[1] / "OpenRigs.Core.dll")
