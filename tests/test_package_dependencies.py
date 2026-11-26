"""A package's embedded workflows name only packages it depends on: itself, or one its csproj references (directly or
through another OpenRigs package, as NuGet installs them). An include path (`Path="OpenRigs.X:Y.bonsai"`) or an XML type
(`assembly=OpenRigs.X`) of an undeclared package works only because every lab workflow installs every OpenRigs package.

The opto layering is held here too: OpenRigs.Trials (the trial loop and its Opto port) depends on no opto system, an opto
adapter (OpenRigs.Opto.*) on Trials and its own driver, never on another adapter, and no driver depends on Trials.
"""

import re
from pathlib import Path

SRC = Path(__file__).parents[1] / "src"

# Hidden dependencies older than this check, each to be declared or removed (BACKLOG.md). Declaring one that points back up
# the graph would make a cycle: Devices -> Vision (Vision -> Trials -> Devices), Logging -> Devices (Devices -> Logging).
KNOWN_HIDDEN = {
    ("OpenRigs.Devices", "OpenRigs.Audio"): "DomeHardware includes SpeakerMixer",
    ("OpenRigs.Devices", "OpenRigs.Vision"): "DomeHardware includes ProjectorDisplay (a cycle if declared)",
    ("OpenRigs.Devices", "OpenRigs.Control"): "the pieces' DeclareDeviceControls and ready stages",
    ("OpenRigs.Devices", "OpenRigs.Video"): "the camera pieces' writers and triggers",
    ("OpenRigs.Logging", "OpenRigs.Devices"): "TaskLogs includes ZapitLogs, ExperimentLogs names Devices types (a cycle if declared)",
    ("OpenRigs.Logging", "OpenRigs.Control"): "ExperimentLogs' controller",
    ("OpenRigs.Logging", "OpenRigs.HarpDevices"): "ExperimentRecords' Harp devices",
    ("OpenRigs.Logging", "OpenRigs.Manifest"): "ExperimentRecords' manifest",
    ("OpenRigs.Logging", "OpenRigs.Video"): "LogVideoFfmpeg's writer",
}

INCLUDE = re.compile(r'Path="(OpenRigs\.[A-Za-z.]+):')
ASSEMBLY = re.compile(r'assembly=(OpenRigs\.[A-Za-z.]+)"')
PROJECT_REFERENCE = re.compile(r'<ProjectReference Include="[^"]*?[\\/](OpenRigs\.[A-Za-z.]+)\.csproj"')
PACKAGE_REFERENCE = re.compile(r'<PackageReference Include="([^"]+)"')


def packages() -> dict[str, str]:
    """Each packed OpenRigs project's csproj text, by name (test projects and OpenRigs.Tests are not packed)."""
    return {path.stem: path.read_text(encoding="utf-8-sig") for path in SRC.glob("OpenRigs.*/OpenRigs.*.csproj")
            if not path.stem.endswith(".Tests") and path.stem != "OpenRigs.Tests" and path.stem == path.parent.name}


def closure(name: str, projects: dict[str, str]) -> set[str]:
    """The OpenRigs packages `name` depends on, directly or through another."""
    found: set[str] = set()
    todo = [name]
    while todo:
        for reference in PROJECT_REFERENCE.findall(projects[todo.pop()]):
            if reference not in found:
                found.add(reference)
                todo.append(reference)
    return found


def hidden() -> dict[tuple[str, str], list[str]]:
    """Each (package, undeclared package) an embedded workflow names, with the workflows that name it."""
    projects = packages()
    found: dict[tuple[str, str], list[str]] = {}
    for name in projects:
        allowed = closure(name, projects) | {name}
        for workflow in sorted((SRC / name).rglob("*.bonsai")):
            text = workflow.read_text(encoding="utf-8-sig")
            for used in sorted(set(INCLUDE.findall(text)) | set(ASSEMBLY.findall(text))):
                if used not in allowed:
                    found.setdefault((name, used), []).append(workflow.name)
    return found


def test_a_package_s_workflows_name_only_packages_it_depends_on():
    new = {pair: files for pair, files in hidden().items() if pair not in KNOWN_HIDDEN}
    assert not new, "embedded workflows name packages their package does not depend on; add a ProjectReference (or remove the use):\n" + "\n".join(
        f"  {package} -> {used}: {', '.join(files)}" for (package, used), files in sorted(new.items()))


def test_the_known_hidden_dependencies_are_still_hidden():
    stale = sorted(set(KNOWN_HIDDEN) - set(hidden()))
    assert not stale, f"declared or gone now: take these off KNOWN_HIDDEN: {stale}"


def test_the_trial_loop_depends_on_no_opto_system_and_no_driver_on_the_loop():
    projects = packages()
    adapters = sorted(name for name in projects if name.startswith("OpenRigs.Opto."))
    assert adapters == ["OpenRigs.Opto.ZapitDaq", "OpenRigs.Opto.ZapitMatlab"]
    trials = closure("OpenRigs.Trials", projects)
    assert not trials & {"OpenRigs.Zapit", *adapters}, f"OpenRigs.Trials depends on an opto system: {sorted(trials)}"
    vendor = {"Bonsai.DAQmx", "Bonsai.Pylon"}
    for name in trials | {"OpenRigs.Trials"}:
        assert not vendor & set(PACKAGE_REFERENCE.findall(projects[name])), f"OpenRigs.Trials gets a vendor wrapper through {name}"
    for driver in ("OpenRigs.Zapit", "OpenRigs.Devices"):
        assert not closure(driver, projects) & {"OpenRigs.Trials", *adapters}, f"{driver} (a driver) depends on the trial loop or an adapter"
    for adapter in adapters:
        assert "OpenRigs.Trials" in closure(adapter, projects), f"{adapter} does not depend on the trial loop"
        assert not closure(adapter, projects) & (set(adapters) - {adapter}), f"{adapter} depends on another adapter"
    assert "OpenRigs.Zapit" in closure("OpenRigs.Opto.ZapitDaq", projects)
    assert "OpenRigs.Zapit" not in closure("OpenRigs.Opto.ZapitMatlab", projects), "MATLAB Zapit's adapter needs no ZapitDaq"
