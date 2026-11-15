import re
import zipfile
from pathlib import Path

from ruamel.yaml import YAML

from open_rigs.rigs import workflow_lint as lint
from open_rigs.rigs.lab_rig import LabRig

ROOT = Path(__file__).parents[2]
HEAD = ('<WorkflowBuilder Version="2.9.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:rx="clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core" xmlns="https://bonsai-rx.org/2018/workflow"><Workflow><Nodes>')


def include(path: str, inner: str = "") -> str:
    return f'<Expression xsi:type="IncludeWorkflow" Path="{path}">{inner}</Expression>'


def test_the_block_subjects_are_what_each_block_declares():
    for path, subjects in lint.BLOCK_SUBJECTS.items():
        package, resource = path.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        declared = dict((name, f"rx:{kind}") for kind, name in re.findall(r'xsi:type="rx:(\w+Subject)"[^>]*>\s*<(?:rx:)?Name>([\w.-]+)<', text))
        assert {name: declared.get(name) for name in subjects} == subjects
    for outer, inner in lint.CARRIED_BLOCKS.items():
        package, resource = outer.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        assert f'Path="{inner}"' in text


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
    (tmp_path / "src").mkdir(parents=True)
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

def test_every_ready_stage_is_announced_by_an_announce_stage_and_the_mixer_sends_mixer_started():
    for path in (ROOT / "src").glob("OpenRigs.*/**/*.bonsai"):
        if not path.relative_to(ROOT / "src").parts[0].endswith(".Tests"):
            assert lint.ready_announcer_problems(lint.ET.fromstring(path.read_text(encoding="utf-8-sig").encode("utf-8"))) == [], path
    mixer = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    assert lint.mixer_ready_problems(lint.ET.fromstring(mixer.encode("utf-8"))) == []
    silent = mixer.replace("<core:Stage>MixerStarted</core:Stage>", "<core:Stage>Mixer</core:Stage>")
    assert silent != mixer
    assert lint.mixer_ready_problems(lint.ET.fromstring(silent.encode("utf-8"))) == [
        "SpeakerMixer does not multicast MixerStarted into ReadyStages once the mixer has started"]


def test_an_announcer_that_completes_is_refused():
    taken = (ROOT / "src" / "OpenRigs.Core.Tests" / "ReadyStagesClosedByATake.bonsai").read_text(encoding="utf-8-sig")
    assert "ReadyStages is fed other than by one AnnounceStage" in lint.ready_announcer_problems(lint.ET.fromstring(taken.encode("utf-8")))


def piece(path: str, **props: str) -> str:
    return include(path, "".join(f"<{k}>{v}</{k}>" for k, v in props.items()))


def board_line(path: str) -> str:
    return piece(path, Board="BehaviorBoard-Main", BoardCommandsSubjectName="BehaviorBoard-Main.Commands",
                 BoardDataSubjectName="BehaviorBoard-Main.Data")


def named(path: str, name: str) -> str:
    return piece(path, Name=name, CommandsSubjectName=f"{name}.Commands", DataSubjectName=f"{name}.Data")


def camera(name: str) -> str:
    return piece(lint.CAMERA, Name=name, Board="BehaviorBoard-Main", BoardDataSubjectName="BehaviorBoard-Main.Data",
                 DataSubjectName=f"{name}.Data")


# The acceptance fixture: a rig no workflow has yet, a board clock, wheel and camera with DomeHardware's lick detectors
# and spout stage, made of the lab's pieces with no hardware code of its own.
MIXED = (include(lint.READ_LAB_RIG) + piece(lint.READY_GATE, ReadyWaitsFor="HarpClockSet, SpoutsHomed, Camera-Overhead")
         + named(lint.BOARD, "BehaviorBoard-Main") + board_line(lint.CLOCK_FROM_BOARD)
         + board_line(lint.DEVICES + "BehaviorBoard.RunningWheel.bonsai")
         + named(lint.LICKETY_SPLIT, "LicketySplit-Left") + named(lint.LICKETY_SPLIT, "LicketySplit-Right")
         + include(lint.LICK_SPOUT_STAGE) + camera("Camera-Overhead")
         + piece(lint.CAMERA_TRIGGERS, Board="BehaviorBoard-Main", BoardCommandsSubjectName="BehaviorBoard-Main.Commands",
                 BoardDataSubjectName="BehaviorBoard-Main.Data", CamerasExpected="Camera-Overhead"))


def test_a_new_combination_of_the_lab_s_devices_needs_no_hardware_code(tmp_path):
    wf = workflow(tmp_path, MIXED)
    assert lint.hardware_problems(wf) == []
    assert lint.required_devices(wf) == [
        ("BehaviorBoard-Main", "behaviorBoard"), ("LicketySplit-Left", "licketySplit"), ("LicketySplit-Right", "licketySplit"),
        ("StepperDriver-A", "stepperDriver"), ("StepperDriver-B", "stepperDriver"), ("Camera-Overhead", "camera")]
    text, records, files = lint.filled_rig(wf, tmp_path, "RIG")
    rig = YAML(typ="safe").load(text)
    LabRig.model_validate(rig)
    assert "timestampGeneratorGen3" not in rig and list(rig["camera"]) == ["overhead"] and list(rig["stepperDriver"]) == ["a", "b"]
    assert rig["behaviorBoard"]["main"]["wiring"]["DO0"]["targets"] == ["Camera-Overhead"]
    assert records == ["BehaviorBoard-Main COM1 SERIAL0 1216", "LicketySplit-Left COM5 SERIAL1 1400", "LicketySplit-Right COM6 SERIAL2 1400",
                       "StepperDriver-A COM3 SERIAL3 1130", "StepperDriver-B COM4 SERIAL4 1130"]
    assert "Camera-Overhead/" in files and "StepperDriver-B/StepperDriver-B.yml" in files and "Camera-Overhead/Camera-Overhead.yml" not in files


def test_the_dome_rig_list_is_expanded_with_the_workflow_s_settings(tmp_path):
    rig_list = include(lint.DEVICES + "DomeHardware.bonsai", "<SpoutStepSize>99</SpoutStepSize>")
    wf = workflow(tmp_path, include(lint.READ_LAB_RIG) + rig_list)
    assert lint.hardware_problems(wf) == [] and lint.speaker_mixer_problems(wf) == []
    assert dict(lint.hardware(wf))[lint.LICK_SPOUT_STAGE]["SpoutStepSize"] == "99"
    assert lint.SPEAKER_MIXER in dict(lint.hardware(wf)) and lint.PROJECTOR_DISPLAY in dict(lint.hardware(wf))
    assert [name for name, _ in lint.required_devices(wf)] == [
        "TimestampGeneratorGen3-Main", "BehaviorBoard-Main", "LicketySplit-Left", "LicketySplit-Right",
        "StepperDriver-A", "StepperDriver-B", "Camera-Face", "Camera-Pupil", "Camera-Body"]
    # The speakers and projector are the rig list's: a task that includes them again has two.
    twice = workflow(tmp_path / "b", include(lint.READ_LAB_RIG) + rig_list + include(lint.SPEAKER_MIXER) + include(lint.PROJECTOR_DISPLAY))
    assert lint.hardware_problems(twice) == ["the workflow includes SpeakerMixer more than once",
                                             "the workflow includes ProjectorDisplay more than once"]
    assert lint.speaker_mixer_problems(twice) == ["the workflow does not include OpenRigs.Audio's SpeakerMixer once"]
    # The rig list holds no ReadLabRig: RigConfigs defined inside it would be defined in terms of itself.
    late = workflow(tmp_path / "c", rig_list + include(lint.READ_LAB_RIG))
    assert lint.hardware_problems(late) == ["ReadLabRig comes after ReadyGate"]


def test_a_broken_combination_is_named(tmp_path):
    broken = (MIXED.replace("Camera-Overhead</CamerasExpected>", "Camera-Face</CamerasExpected>")
              .replace("<DataSubjectName>LicketySplit-Right.Data<", "<DataSubjectName>LicketySplit-Left.Data<"))
    gate = piece(lint.READY_GATE, ReadyWaitsFor="HarpClockSet, SpoutsHomed, Camera-Overhead")
    late = broken.replace(gate, "") + gate + named(lint.CLOCK_FROM_GENERATOR, "TimestampGeneratorGen3-Main")
    assert lint.hardware_problems(workflow(tmp_path, late)) == [
        "the workflow has 2 clocks, not one (ClockFromTimestampGenerator or ClockFromBoard)",
        "BehaviorBoard.BehaviorBoard BehaviorBoard-Main comes before ReadyGate",
        "BehaviorBoard.ClockFromBoard BehaviorBoard-Main comes before ReadyGate",
        "BehaviorBoard.RunningWheel BehaviorBoard-Main comes before ReadyGate",
        "LicketySplit LicketySplit-Left comes before ReadyGate",
        "LicketySplit LicketySplit-Right comes before ReadyGate",
        "LickSpoutStage comes before ReadyGate",
        "SpinnakerCamera Camera-Overhead comes before ReadyGate",
        "CameraTriggers BehaviorBoard-Main comes before ReadyGate",
        "LicketySplit LicketySplit-Right's DataSubjectName is LicketySplit-Left.Data, not LicketySplit-Right.Data",
        "CameraTriggers' CamerasExpected is Camera-Face, not Camera-Overhead"]


def test_a_workflow_s_own_ready_report_is_found_under_any_prefix(tmp_path):
    own = '<Expression xsi:type="Combinator"><Combinator xmlns:p3="clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control" xsi:type="p3:ReportPhase"><p3:Phase>Ready</p3:Phase></Combinator></Expression>'
    assert lint.hardware_problems(workflow(tmp_path, MIXED + own)) == ["the workflow has its own Ready report"]


def test_a_lab_type_is_found_under_the_prefix_a_bonsai_save_gives_it(tmp_path):
    p1 = 'xmlns:p1="clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core" '
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "main.bonsai").write_text(
        HEAD.replace("xmlns=", p1 + "xmlns=") + '<Expression xsi:type="GroupWorkflow"><Name>TaskLogic</Name><Workflow><Nodes>'
        '<Expression xsi:type="Combinator"><Combinator xsi:type="p1:HarpDelay" /></Expression></Nodes><Edges /></Workflow></Expression>'
        "</Nodes><Edges /></Workflow></WorkflowBuilder>", encoding="utf-8")
    assert lint.harp_delays_off_the_clock(lint.Workflow(tmp_path)) == (1, ["HarpDelay"])


def test_a_command_the_workflow_handles_and_does_not_declare_is_found(tmp_path):
    on = '<Expression xsi:type="Combinator"><Combinator xsi:type="c:OnCommand"><c:Name>{}</c:Name></Combinator></Expression>'
    wf = workflow(tmp_path, '<Expression xsi:type="GroupWorkflow" xmlns:c="clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control">'
                  '<Name>Commands</Name><Workflow><Nodes>' + on.format("RewardLeft") + on.format("Flush")
                  + "</Nodes><Edges /></Workflow></Expression><Command><Name>RewardLeft</Name></Command>")
    assert lint.undeclared_commands(wf) == ["Flush"]


def test_a_node_not_in_the_workflow_is_named_not_a_bare_stop_iteration(tmp_path):
    try:
        workflow(tmp_path, "").named("TaskLogic")
    except ValueError as error:
        assert str(error) == r"src\main.bonsai has no node named TaskLogic"
    else:
        raise AssertionError("no error")


def pulse_workflow(tmp_path: Path, nodes: str, edges: str) -> lint.Workflow:
    beh = ('xmlns:beh="clr-namespace:Bonsai.Harp.Behavior;assembly=Bonsai.Harp.Behavior" '
           'xmlns:harp="clr-namespace:Bonsai.Harp;assembly=Bonsai.Harp" '
           'xmlns:orc="clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core" ')
    (tmp_path / "src").mkdir(parents=True)
    (tmp_path / "src" / "main.bonsai").write_text(HEAD.replace("xmlns=", beh + "xmlns=") + nodes + "</Nodes><Edges>" + edges
                                                  + "</Edges></Workflow></WorkflowBuilder>", encoding="utf-8")
    return lint.Workflow(tmp_path)


def test_a_fixed_width_pulse_on_a_reward_valve_is_refused_and_a_calibrated_one_is_not(tmp_path):
    line = '<Expression xsi:type="Combinator"><Combinator xsi:type="IntProperty"><Value>{}</Value></Combinator></Expression>'
    send = '<Expression xsi:type="MulticastSubject"><Name>{}</Name></Expression>'
    edge = '<Edge From="0" To="1" Label="Source1" />'
    valves = {1, 2}
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "a", line.format(3) + send.format("TriggerPulse"), edge), valves) == []
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "b", line.format(2) + send.format("TriggerPulse"), edge), valves) == [
        "TriggerPulse fires a fixed-width pulse on DO2, a RewardValve"]
    computed = '<Expression xsi:type="SubscribeSubject"><Name>RewardValveLines</Name></Expression>' + send.format("TriggerPulse")
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "c", computed, edge), valves) == [
        "TriggerPulse is sent a line no IntProperty gives, so it may be a RewardValve's"]
    written = ('<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreateOutputSetPayload"><beh:OutputSet>DO1</beh:OutputSet></harp:Payload></Expression>'
               '<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreatePulseDO2Payload"><beh:PulseDO2>10</beh:PulseDO2></harp:Payload></Expression>'
               '<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreateOutputSetPayload"><beh:OutputSet>DO3</beh:OutputSet></harp:Payload></Expression>')
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "d", written, ""), valves) == [
        "CreateOutputSetPayload writes DO1, a RewardValve, itself", "CreatePulseDO2Payload writes DO2, a RewardValve, itself"]
    calibrated = ('<Expression xsi:type="Combinator"><Combinator xsi:type="orc:FirePulses" /></Expression>'
                  + send.format("TriggerCalibratedPulse"))
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "e", calibrated, edge), valves) == []
    renamed = ('<Expression xsi:type="IncludeWorkflow" Path="OpenRigs.Devices:BehaviorBoard.PulseController.bonsai">'
               '<TriggerPulseSubjectName>TriggerValve</TriggerPulseSubjectName></Expression>'
               + line.format(1) + send.format("TriggerValve"))
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "f", renamed, '<Edge From="1" To="2" Label="Source1" />'), valves) == [
        "TriggerValve fires a fixed-width pulse on DO1, a RewardValve"]


MF = 'xmlns:mf="clr-namespace:OpenRigs.Manifest;assembly=OpenRigs.Manifest"'


def manifest_node(expected: str) -> str:
    return f'<Expression xsi:type="mf:ExperimentManifest" {MF}><mf:ExpectedFiles>{expected}</mf:ExpectedFiles></Expression>'


LOGS = (include("OpenRigs.Logging:LogTrial.bonsai") + include("OpenRigs.Logging:LogAsync.bonsai")
        + include("OpenRigs.Logging:LogDataSchema.bonsai", "<Name>Settings_Rig</Name>")
        + include("OpenRigs.Logging:LogData.bonsai", "<LogName>BonsaiData_StimulusState</LogName>")
        + include("OpenRigs.Devices:Zapit.ZapitLogs.bonsai") + include("OpenRigs.Devices:BehaviorBoard.BehaviorBoard.bonsai")
        + '<Expression xsi:type="hd:RecordHarpDevices" xmlns:hd="clr-namespace:OpenRigs.HarpDevices;assembly=OpenRigs.HarpDevices" />')
EXPECTED = ("auto;harp;Settings/Settings_Rig_;BonsaiData/BonsaiData_Trial_;BonsaiData/BonsaiData_Async_;BonsaiData/BonsaiData_StimulusState_;"
            "BonsaiData/BonsaiData_ZapitReplies_?Optogenetics;Settings/Settings_ZapitConditions_?Optogenetics")


def test_every_file_a_workflow_writes_is_named_in_expected_files_with_its_condition(tmp_path):
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(EXPECTED))) == []


def test_a_folder_entry_in_place_of_each_log_is_refused(tmp_path):
    folder = EXPECTED.replace("BonsaiData/BonsaiData_Trial_;BonsaiData/BonsaiData_Async_;", "BonsaiData/;")
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(folder))) == [
        "BonsaiData/BonsaiData_Trial_ is written but not in ExpectedFiles (name it, not BonsaiData/)",
        "BonsaiData/BonsaiData_Async_ is written but not in ExpectedFiles (name it, not BonsaiData/)",
        "ExpectedFiles names BonsaiData/, which no node in the workflow writes (name each log instead)"]


def test_a_log_written_under_a_condition_must_be_named_with_it(tmp_path):
    unconditional = EXPECTED.replace("Settings/Settings_ZapitConditions_?Optogenetics", "Settings/Settings_ZapitConditions_")
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(unconditional))) == [
        "ExpectedFiles names Settings/Settings_ZapitConditions_; its block writes it as Settings/Settings_ZapitConditions_?Optogenetics"]


def test_an_entry_no_node_writes_and_missing_auto_or_harp_are_refused(tmp_path):
    stale = EXPECTED.replace("auto;harp;", "") + ";Settings/Settings_Old_"
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(stale))) == [
        "ExpectedFiles names Settings/Settings_Old_, which no node in the workflow writes",
        "the workflow logs devices but ExpectedFiles has no auto",
        "the workflow has RecordHarpDevices but ExpectedFiles has no harp"]


def test_a_writer_whose_file_the_workflow_cannot_name_is_refused(tmp_path):
    writer = '<Expression xsi:type="Combinator"><Combinator xsi:type="io:CsvWriter" xmlns:io="clr-namespace:Bonsai.IO;assembly=Bonsai.System" /></Expression>'
    unnamed = include("OpenRigs.Logging:LogData.bonsai")
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + writer + unnamed + manifest_node(EXPECTED))) == [
        "a CsvWriter node writes a file the workflow's XML cannot name: log through OpenRigs.Logging's LogData",
        "OpenRigs.Logging:LogData.bonsai has no LogName written in the workflow, so the file it writes cannot be named"]


def test_a_combinator_with_no_type_is_read_not_crashed_on(tmp_path):
    untyped = '<Expression xsi:type="Combinator"><Combinator /></Expression>'
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + untyped + manifest_node(EXPECTED))) == []


def test_a_workflow_without_a_manifest_has_nothing_to_name(tmp_path):
    assert lint.expected_files_problems(workflow(tmp_path, LOGS)) == []


def test_the_fixed_logs_are_the_names_their_blocks_write():
    for path, logs in lint.FIXED_LOGS.items():
        package, resource = path.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        if 'Path="OpenRigs.Logging:LogAsync.bonsai"' in text:  # a block that includes LogAsync writes its log too
            text += (ROOT / "src" / "OpenRigs.Logging" / "LogAsync.bonsai").read_text(encoding="utf-8-sig")
        assert all(f">{name}<" in text for name, _ in logs), path


def test_the_rng_seed_event_is_fed_by_the_trial_start_subject_alone(tmp_path):
    def logic(subject: str) -> lint.Workflow:
        sub = f'<Expression xsi:type="SubscribeSubject"><Name>{subject}</Name></Expression>'
        seed = '<Expression xsi:type="ExternalizedMapping"><Name>RngSeed</Name></Expression>'
        return pulse_workflow(tmp_path / subject, '<Expression xsi:type="GroupWorkflow"><Name>TaskLogic</Name><Workflow><Nodes>' + sub + seed
                              + '</Nodes><Edges><Edge From="0" To="1" Label="Source1" /></Edges></Workflow></Expression>', "")

    assert lint.rng_seed_problems(logic("TrialStart")) == []
    assert lint.rng_seed_problems(logic("TrialGo")) == ["RngSeed is not fed by TrialStart alone"]


def test_each_include_read_from_the_package_is_found_by_its_description(tmp_path, monkeypatch):
    for path, signature in lint.SIGNATURES.items():
        package, resource = path.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        assert f"<Description>{signature}" in text, path
    # A workflow repo reads it from the OpenRigs.Devices package Bonsai.config pins, embedded in the DLL.
    rig_list = (ROOT / "src" / "OpenRigs.Devices" / "DomeHardware.bonsai").read_bytes()
    (tmp_path / ".bonsai").mkdir()
    (tmp_path / ".bonsai" / "Bonsai.config").write_text('<Packages><Package id="OpenRigs.Devices" version="0.1.1-x.2" /></Packages>')
    (tmp_path / "local_packages").mkdir()
    with zipfile.ZipFile(tmp_path / "local_packages" / "OpenRigs.Devices.0.1.1-x.2.nupkg", "w") as z:
        z.writestr("lib/net472/OpenRigs.Devices.dll", b"MZ\x00<WorkflowBuilder>other</WorkflowBuilder>\x00\x10" + rig_list + b"\x00\xff")
    monkeypatch.setattr(lint, "LIB_SRC", tmp_path / "nowhere")
    wf = workflow(tmp_path, include(lint.DEVICES + "DomeHardware.bonsai", "<ReadyWaitsFor>HarpClockSet, SpoutsHomed, Camera-Face, Camera-Pupil, Camera-Body</ReadyWaitsFor>"))
    assert len(lint.required_devices(wf)) == 9

def test_zapit_and_the_manual_rewards_are_the_lab_s_includes(tmp_path):
    def root(resource: str) -> lint.ET.Element:
        return lint.ET.fromstring((ROOT / "src" / "OpenRigs.Devices" / resource).read_bytes())
    assert lint.zapit_for_task_problems(root("Zapit/ConnectForTask.bonsai")) == []
    assert lint.manual_rewards_drop_problems(root("BehaviorBoard/ManualRewards.bonsai")) == []
    commands = '<Command><Name>RewardLeft</Name></Command><Command><Name>RewardRight</Name></Command>'
    group = '<Expression xsi:type="GroupWorkflow"><Name>Commands</Name><Workflow><Nodes /><Edges /></Workflow></Expression>'
    wf = workflow(tmp_path, include(lint.ZAPIT_FOR_TASK) + include(lint.MANUAL_REWARDS) + group + commands)
    assert lint.zapit_include_problems(wf) == [] and lint.manual_reward_problems(wf) == [] and lint.undeclared_commands(wf) == []
    undeclared = workflow(tmp_path / "b", include(lint.MANUAL_REWARDS) + group)
    assert lint.undeclared_commands(undeclared) == ["RewardLeft", "RewardRight"]
    assert lint.zapit_include_problems(undeclared) == ["the workflow does not include Zapit.ConnectForTask once"]


def test_the_shared_experiment_logs_are_the_lab_s_two_includes(tmp_path):
    both = include("OpenRigs.Logging:LogLabExperiment.bonsai") + include("OpenRigs.Logging:LogControlEvents.bonsai")
    assert lint.experiment_log_problems(workflow(tmp_path, both)) == []
    own = include("OpenRigs.Logging:LogController.bonsai") + include("OpenRigs.Logging:LogDataSchema.bonsai", "<Name>Settings_Rig</Name>")
    assert lint.experiment_log_problems(workflow(tmp_path / "b", own)) == [
        "the workflow does not include LogLabExperiment once", "the workflow does not include LogControlEvents once",
        "the workflow includes LogController itself (LogLabExperiment does)", "the workflow logs Settings_Rig itself (LogLabExperiment does)"]
    # LogLabExperiment is its own top-level node, before the hardware (every device's log waits for its PathPrefix).
    gate = include(lint.READY_GATE)
    assert lint.experiment_log_problems(workflow(tmp_path / "d", gate + both)) == ["LogLabExperiment comes after the hardware"]
    grouped = f'<Expression xsi:type="GroupWorkflow"><Workflow><Nodes>{both}</Nodes><Edges /></Workflow></Expression>'
    assert lint.experiment_log_problems(workflow(tmp_path / "e", grouped + gate)) == ["LogLabExperiment is not a top-level node of its own"]
    assert lint.experiment_log_problems(workflow(tmp_path / "f", both + gate)) == []
    assert lint.expected_files_problems(workflow(tmp_path / "c", both + manifest_node("BonsaiData/BonsaiData_Async_;Settings/Settings_Rig_;Settings/Settings_Experiment_"))) == []
