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


def test_the_speaker_mixer_starts_no_sooner_than_3_s_after_start():
    # Deliberate: a fault while the sound card boots can blue-screen the PC. Never remove or shorten it.
    text = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    assert lint.mixer_boot_delay_problems(lint.ET.fromstring(text.encode("utf-8"))) == []
    shortened = text.replace("<rx:DueTime>PT3S</rx:DueTime>", "<rx:DueTime>PT1S</rx:DueTime>")
    assert lint.mixer_boot_delay_problems(lint.ET.fromstring(shortened.encode("utf-8"))) == ["MixerBootDelay is not a DelaySubscription of PT3S"]


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


AUD = 'xmlns:aud="clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio" xmlns:mix="clr-namespace:Bonsai.Mixer;assembly=Bonsai.Mixer" '


def sound_workflow(tmp_path: Path, nodes: str, edges: str) -> lint.Workflow:
    (tmp_path / "src").mkdir(parents=True)
    (tmp_path / "src" / "main.bonsai").write_text(HEAD.replace("xmlns=", AUD + "xmlns=") + nodes + "</Nodes><Edges>" + edges + "</Edges></Workflow></WorkflowBuilder>", encoding="utf-8")
    return lint.Workflow(tmp_path)


def test_a_sound_needs_its_level_from_the_task_file_and_the_shared_mixer(tmp_path):
    nodes = ('<Expression xsi:type="IncludeWorkflow" Path="OpenRigs.Audio:SpeakerMixer.bonsai" />'
             '<Expression xsi:type="SubscribeSubject"><Name>TaskConfig</Name></Expression>'
             '<Expression xsi:type="MemberSelector"><Selector>TaskParameters.BackgroundNoise.LevelDbfs</Selector></Expression>'
             '<Expression xsi:type="InputMapping"><PropertyMappings><Property Name="LevelDbfs" Selector="Item1" /></PropertyMappings></Expression>'
             '<Expression xsi:type="Combinator"><Combinator xsi:type="aud:SpeakerBuffer" /></Expression>')
    wf = sound_workflow(tmp_path, nodes, '<Edge From="1" To="2" Label="Source1" /><Edge From="2" To="3" Label="Source1" /><Edge From="3" To="4" Label="Source1" />')
    assert lint.speaker_mixer_problems(wf) == [] and lint.sound_level_problems(wf) == []
    unmapped = sound_workflow(tmp_path / "b", nodes + '<Expression xsi:type="Combinator"><Combinator xsi:type="mix:StartMixer" /></Expression>', "")
    assert lint.sound_level_problems(unmapped) == ["aud:SpeakerBuffer is not given LevelDbfs from the task file's levelDbfs"]
    assert lint.speaker_mixer_problems(unmapped) == ["the workflow uses Bonsai.Mixer's StartMixer itself"]