from pathlib import Path

from open_rigs.rigs import workflow_lint as lint

ROOT = Path(__file__).parents[2]
HEAD = ('<WorkflowBuilder Version="2.9.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:rx="clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core" '
        'xmlns:core="clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core" xmlns="https://bonsai-rx.org/2018/workflow">'
        '<Workflow><Nodes>')
SOURCE = '<Expression xsi:type="SubscribeSubject"><Name>Camera-Face</Name></Expression>'
INPUT = '<Expression xsi:type="WorkflowInput"><Name>Source1</Name></Expression>'
MAPPING = '<Expression xsi:type="PropertyMapping"><PropertyMappings><Property Name="Stage" Selector="Name" /></PropertyMappings></Expression>'
ANNOUNCE = '<Expression xsi:type="Combinator"><Combinator xsi:type="core:AnnounceStage"><core:Stage /></Combinator></Expression>'
ZIP = '<Expression xsi:type="Combinator"><Combinator xsi:type="rx:Zip" /></Expression>'


def problems(nodes: str, edges: list[tuple[int, int, str]]) -> list[str]:
    text = HEAD + nodes + "</Nodes><Edges>" + "".join(f'<Edge From="{a}" To="{b}" Label="{label}" />' for a, b, label in edges)
    return lint.shared_mapping_problems(lint.ET.fromstring((text + "</Edges></Workflow></WorkflowBuilder>").encode("utf-8")))


def test_no_lab_workflow_maps_a_property_from_a_shared_source():
    for path in (ROOT / "src").glob("OpenRigs.*/**/*.bonsai"):
        assert lint.shared_mapping_problems(lint.ET.fromstring(path.read_text(encoding="utf-8-sig").encode("utf-8"))) == [], path


def test_a_mapping_whose_source_also_feeds_another_node_is_refused():
    # SpinnakerCamera before 16 Nov 2026: the camera's subject fed its Zip and the Stage mapping, so AnnounceStage
    # read its Stage before the value came ("AnnounceStage has no Stage to announce").
    assert problems(SOURCE + MAPPING + ANNOUNCE + ZIP, [(0, 1, "Source1"), (1, 2, "Source2"), (0, 3, "Source1")]) == [
        "the PropertyMapping of Stage onto AnnounceStage shares its source (SubscribeSubject Camera-Face) with another node: give it its own"]


def test_a_mapping_with_its_own_source_is_allowed():
    assert problems(SOURCE + MAPPING + ANNOUNCE + ZIP + SOURCE,
                    [(0, 1, "Source1"), (1, 2, "Source2"), (4, 3, "Source1")]) == []


def test_a_source_that_feeds_only_the_mapping_and_its_target_is_allowed():
    # The value comes with each element the target is given (PulseOutputs' pulse widths).
    assert problems(SOURCE + MAPPING + ANNOUNCE, [(0, 1, "Source1"), (1, 2, "Source2"), (0, 2, "Source1")]) == []


def test_an_include_s_input_is_its_caller_s_to_give():
    assert problems(INPUT + MAPPING + ANNOUNCE + ZIP, [(0, 1, "Source1"), (1, 2, "Source2"), (0, 3, "Source1")]) == []
