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


def test_the_speaker_mixer_plays_no_sound_sent_while_it_boots():
    # AppendBuffer subscribes only once the mixer starts: a BehaviorSubject would replay a buffer sent during the boot, late.
    text = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    assert lint.mixer_replay_problems(lint.ET.fromstring(text.encode("utf-8"))) == []
    direct = text.replace("<Name>MixerStimulusSounds</Name>", "<Name>StimulusSounds</Name>")
    assert lint.mixer_replay_problems(lint.ET.fromstring(direct.encode("utf-8"))) == [
        "StimulusSounds reaches the mixer other than through the PublishSubject MixerStimulusSounds, fed by a CheckMixerStarted of StimulusSounds and Mixer"]


def test_a_stimulus_or_penalty_sound_sent_while_the_mixer_boots_stops_the_workflow():
    # The relay alone dropped it silently while the trial log recorded it as sent.
    text = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    unchecked = text.replace('<Edge From="36" To="39" Label="Source1" />', "").replace(
        '<Edge From="39" To="37" Label="Source1" />', '<Edge From="36" To="37" Label="Source1" />')
    assert unchecked != text
    assert lint.mixer_replay_problems(lint.ET.fromstring(unchecked.encode("utf-8"))) == [
        "PenaltySounds reaches the mixer other than through the PublishSubject MixerPenaltySounds, fed by a CheckMixerStarted of PenaltySounds and Mixer"]
    misnamed = text.replace("<aud:Sound>StimulusSounds</aud:Sound>", "<aud:Sound>PenaltySounds</aud:Sound>")
    assert lint.mixer_replay_problems(lint.ET.fromstring(misnamed.encode("utf-8"))) == [
        "StimulusSounds reaches the mixer other than through the PublishSubject MixerStimulusSounds, fed by a CheckMixerStarted of StimulusSounds and Mixer"]


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


LAYOUT = '<VisualizerLayout xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />'
SHIM = ("$repo = Split-Path -Parent $PSScriptRoot\r\n& uv sync --project $repo -q\r\n"
        "& powershell -File (Join-Path $repo '.venv\\Lib\\site-packages\\open_rigs\\scripts\\setup-bonsai.ps1') -Environment $PSScriptRoot\r\n")


def test_the_layout_is_copied_by_setup_itself_or_by_the_lab_s_one_copy_it_runs(tmp_path):
    wf = workflow(tmp_path, "")
    (tmp_path / "config").mkdir()
    (tmp_path / "config" / "main.layout").write_text(LAYOUT, encoding="utf-8")
    (tmp_path / ".bonsai").mkdir()
    setup = tmp_path / ".bonsai" / "Setup.ps1"
    setup.write_text(SHIM, encoding="utf-8")
    assert lint.layout_problems(wf) == []
    setup.write_text("& .\\Bonsai.exe --no-editor\r\n", encoding="utf-8")
    assert lint.layout_problems(wf) == [".bonsai\\Setup.ps1 does not copy config\\main.layout"]


def test_the_lab_s_bonsai_setup_keeps_the_workflow_s_nuget_config_and_restores():
    text = lint.SETUP_BONSAI.read_text(encoding="utf-8-sig")
    keep, expand, back = (text.index(s) for s in ('Move-Item -Path "NuGet.config" "temp.config"', "Expand-Archive",
                                                   'Move-Item -Path "temp.config" "NuGet.config"'))
    assert keep < expand < back
    assert "releases/download/$version/Bonsai.zip" in text and text.rstrip().endswith("exit $restoreExit")


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