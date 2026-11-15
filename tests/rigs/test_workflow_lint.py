import re
from pathlib import Path

from open_rigs.rigs import workflow_lint as lint

ROOT = Path(__file__).parents[2]
HEAD = ('<WorkflowBuilder Version="2.9.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:rx="clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core" xmlns="https://bonsai-rx.org/2018/workflow"><Workflow><Nodes>')


def test_the_block_subjects_are_what_each_block_declares():
    for path, subjects in lint.BLOCK_SUBJECTS.items():
        package, resource = path.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        declared = dict((name, f"rx:{kind}") for kind, name in re.findall(r'xsi:type="rx:(\w+Subject)"[^>]*>\s*<rx:Name>(\w+)<', text))
        assert {name: declared.get(name) for name in subjects} == subjects


def workflow(tmp_path: Path, nodes: str) -> lint.Workflow:
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "main.bonsai").write_text(HEAD + nodes + "</Nodes><Edges /></Workflow></WorkflowBuilder>", encoding="utf-8")
    return lint.Workflow(tmp_path)


def test_a_per_trial_multicast_into_a_publish_subject_is_found_and_a_block_s_behavior_subject_is_not(tmp_path):
    per_trial = ('<Expression xsi:type="rx:SelectMany"><Workflow><Nodes>'
                 '<Expression xsi:type="MulticastSubject"><Name>{}</Name></Expression></Nodes><Edges /></Workflow></Expression>')
    wf = workflow(tmp_path, '<Expression xsi:type="rx:PublishSubject"><rx:Name>Trial</rx:Name></Expression>'
                  f'<Expression xsi:type="IncludeWorkflow" Path="{lint.ZAPIT_INCLUDE}" />'
                  + per_trial.format("Trial") + per_trial.format("ZapitCommands"))
    assert lint.subjects_closed_by_a_nested_multicast(wf) == ["Trial"]
