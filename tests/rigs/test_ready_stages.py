"""The stages hardware_problems makes ReadyGate wait for (workflow_lint.READY_STAGES, and each camera's Name) are the
ones the pieces announce, so renaming one in a piece fails here, not on a rig as a Ready that never comes."""

from pathlib import Path

from open_rigs.rigs import workflow_lint as lint

SRC = Path(__file__).parents[2] / "src"
STAGE = "{clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core}Stage"
# A piece that announces from a file it includes, by the piece (none today).
ANNOUNCED_IN: dict[str, str] = {}


def source(path: str) -> Path:
    package, name = path.split(":")
    *folders, stem, ext = name.split(".")
    return SRC.joinpath(package, *folders, f"{stem}.{ext}")


def announcements(file: Path) -> list[tuple[str, str]]:
    """Each AnnounceStage in the file as (the subject it multicasts into, its Stage: the literal, or "<mapped from
    Selector>" when a PropertyMapping sets it)."""
    found = []
    root = lint.ET.fromstring(file.read_text(encoding="utf-8-sig").encode("utf-8"))
    for nodes, edges in map(lint.nodes_and_edges, root.iter(lint.W + "Workflow")):
        for i, node in enumerate(nodes):
            if lint.kind(node) != "core:AnnounceStage":
                continue
            stage = lint.child(node, lint.W + "Combinator").findtext(STAGE) or ""
            for a, b, _ in edges:
                if b == i and nodes[a].get(lint.XSI) == "PropertyMapping":
                    stage = "<mapped from " + next(p.get("Selector", "") for p in nodes[a].iter(lint.W + "Property")) + ">"
            found += [(nodes[b].findtext(lint.W + "Name") or "", stage) for a, b, _ in edges if a == i]
    return found


def test_each_piece_announces_the_stage_ready_waits_for():
    for path, stage in lint.READY_STAGES.items():
        read = [("ReadyStages", lint.CLOCK_READ)] if path == lint.CLOCK_FROM_BOARD else []
        assert announcements(source(ANNOUNCED_IN.get(path, path))) == [("ReadyStages", stage)] + read, path
    # A camera announces its own Name, mapped from its rig.yml entry's.
    assert announcements(source(lint.CAMERA)) == [("ReadyStages", "<mapped from Name>")]
    # HarpClockAgrees announces its Device, mapped from the device's own subject (its rig.yml entry's Name).
    assert announcements(source(lint.HARP_CLOCK_AGREES)) == [("ReadyStages", "<mapped from Name>")]


def test_no_other_piece_announces_into_ready_stages():
    pieces = set(lint.READY_STAGES) | {lint.CAMERA, lint.HARP_CLOCK_AGREES} | set(ANNOUNCED_IN.values())
    for file in SRC.glob("OpenRigs.*/**/*.bonsai"):
        if file.relative_to(SRC).parts[0].endswith(".Tests") or file in {source(p) for p in pieces}:
            continue
        assert ("ReadyStages" not in [subject for subject, _ in announcements(file)]), file


def test_camera_triggers_cameras_open_starts_the_triggers_and_is_not_a_ready_stage():
    # CamerasOpen keeps StartCameras open (an announcer never completes); Ready waits for each camera's first frame,
    # which comes only once the triggers have started.
    assert announcements(source(lint.CAMERA_TRIGGERS)) == [("StartCameras", "CamerasOpen")]
