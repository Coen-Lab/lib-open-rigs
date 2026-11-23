import re
import zipfile
from pathlib import Path

import pytest

from ruamel.yaml import YAML

from open_rigs.rigs import workflow_lint as lint

import include_rules as rules
from open_rigs.rigs.lab_rig import LabRig

ROOT = Path(__file__).parents[2]
HEAD = ('<WorkflowBuilder Version="2.9.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:rx="clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core" xmlns="https://bonsai-rx.org/2018/workflow"><Workflow><Nodes>')


def include(path: str, inner: str = "") -> str:
    return f'<Expression xsi:type="IncludeWorkflow" Path="{path}">{inner}</Expression>'


def valve(instance: str, board: str = "BehaviorBoard-Main") -> str:
    """A RewardValve piece, as a task includes one per valve after Rewards."""
    return include(lint.REWARD_VALVE, f"<Name>RewardValve-{instance}</Name><Board>{board}</Board>"
                   f"<BoardDataSubjectName>{board}.Data</BoardDataSubjectName>"
                   f"<BoardTriggerCalibratedPulseSubjectName>{board}.TriggerCalibratedPulse</BoardTriggerCalibratedPulseSubjectName>")


VALVES = valve("Left") + valve("Right")


def test_the_block_subjects_are_what_each_block_declares():
    for path, subjects in lint.BLOCK_SUBJECTS.items():
        package, resource = path.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        declared = dict((name, f"rx:{kind}") for kind, name in re.findall(r'xsi:type="rx:(\w+Subject)"[^>]*>\s*<(?:rx:)?Name>([\w.-]+)<', text))
        assert {name: declared.get(name) for name in subjects} == subjects
    for outer, inners in lint.CARRIED_BLOCKS.items():
        package, resource = outer.split(":")
        text = (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")
        assert all(f'Path="{inner}"' in text for inner in inners), outer


def test_the_speaker_mixer_starts_no_sooner_than_3_s_after_start():
    # Deliberate: a fault while the sound card boots can blue-screen the PC. Never remove or shorten it.
    text = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    assert rules.mixer_boot_delay_problems(lint.ET.fromstring(text.encode("utf-8"))) == []
    shortened = text.replace("<rx:DueTime>PT3S</rx:DueTime>", "<rx:DueTime>PT1S</rx:DueTime>")
    assert rules.mixer_boot_delay_problems(lint.ET.fromstring(shortened.encode("utf-8"))) == ["MixerBootDelay is not a DelaySubscription of PT3S"]


def test_the_speaker_mixer_plays_no_sound_sent_while_it_boots():
    # AppendBuffer subscribes only once the mixer starts: a BehaviorSubject would replay a buffer sent during the boot, late.
    text = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    assert rules.mixer_replay_problems(lint.ET.fromstring(text.encode("utf-8"))) == []
    direct = text.replace("<Name>MixerStimulusSounds</Name>", "<Name>StimulusSounds</Name>")
    assert rules.mixer_replay_problems(lint.ET.fromstring(direct.encode("utf-8"))) == [
        "StimulusSounds reaches the mixer other than through the PublishSubject MixerStimulusSounds, fed by a CheckMixerStarted of StimulusSounds and Mixer"]


def test_a_stimulus_or_penalty_sound_sent_while_the_mixer_boots_stops_the_workflow():
    # The relay alone dropped it silently while the trial log recorded it as sent.
    text = (ROOT / "src" / "OpenRigs.Audio" / "SpeakerMixer.bonsai").read_text(encoding="utf-8-sig")
    unchecked = text.replace('<Edge From="36" To="39" Label="Source1" />', "").replace(
        '<Edge From="39" To="37" Label="Source1" />', '<Edge From="36" To="37" Label="Source1" />')
    assert unchecked != text
    assert rules.mixer_replay_problems(lint.ET.fromstring(unchecked.encode("utf-8"))) == [
        "PenaltySounds reaches the mixer other than through the PublishSubject MixerPenaltySounds, fed by a CheckMixerStarted of PenaltySounds and Mixer"]
    misnamed = text.replace("<aud:Sound>StimulusSounds</aud:Sound>", "<aud:Sound>PenaltySounds</aud:Sound>")
    assert rules.mixer_replay_problems(lint.ET.fromstring(misnamed.encode("utf-8"))) == [
        "StimulusSounds reaches the mixer other than through the PublishSubject MixerStimulusSounds, fed by a CheckMixerStarted of StimulusSounds and Mixer"]


def workflow(tmp_path: Path, nodes: str) -> lint.Workflow:
    (tmp_path / "src").mkdir(parents=True, exist_ok=True)
    (tmp_path / "src" / "main.bonsai").write_text(HEAD + nodes + "</Nodes><Edges /></Workflow></WorkflowBuilder>", encoding="utf-8")
    return lint.Workflow(tmp_path)


def test_a_per_trial_multicast_into_a_publish_subject_is_found_and_a_block_s_behavior_subject_is_not(tmp_path):
    per_trial = ('<Expression xsi:type="rx:SelectMany"><Workflow><Nodes>'
                 '<Expression xsi:type="MulticastSubject"><Name>{}</Name></Expression></Nodes><Edges /></Workflow></Expression>')
    wf = workflow(tmp_path, '<Expression xsi:type="rx:PublishSubject"><rx:Name>Trial</rx:Name></Expression>'
                  f'<Expression xsi:type="IncludeWorkflow" Path="{lint.ZAPIT_FOR_TASK}" />'
                  + per_trial.format("Trial") + per_trial.format("Zapit.Commands"))
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
             '<Expression xsi:type="MemberSelector"><Selector>TaskParameters.Reward.Tone.LevelDbfs</Selector></Expression>'
             '<Expression xsi:type="InputMapping"><PropertyMappings><Property Name="LevelDbfs" Selector="Item1" /></PropertyMappings></Expression>'
             '<Expression xsi:type="Combinator"><Combinator xsi:type="aud:SpeakerBuffer" /></Expression>')
    wf = sound_workflow(tmp_path, nodes, '<Edge From="1" To="2" Label="Source1" /><Edge From="2" To="3" Label="Source1" /><Edge From="3" To="4" Label="Source1" />')
    assert lint.speaker_mixer_problems(wf) == [] and lint.sound_level_problems(wf) == []
    unmapped = sound_workflow(tmp_path / "b", nodes + '<Expression xsi:type="Combinator"><Combinator xsi:type="mix:StartMixer" /></Expression>', "")
    assert lint.sound_level_problems(unmapped) == ["aud:SpeakerBuffer is not given LevelDbfs from the task file's levelDbfs"]
    assert lint.speaker_mixer_problems(unmapped) == ["the workflow uses Bonsai.Mixer's StartMixer itself"]

def test_every_ready_stage_is_announced_by_an_announce_stage():
    for path in (ROOT / "src").glob("OpenRigs.*/**/*.bonsai"):
        if not path.relative_to(ROOT / "src").parts[0].endswith(".Tests"):
            assert lint.ready_announcer_problems(lint.ET.fromstring(path.read_text(encoding="utf-8-sig").encode("utf-8"))) == [], path


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


def lick(name: str) -> str:
    return piece(lint.LICKETY_SPLIT, Name=name, CommandsSubjectName=f"{name}.Commands", DataSubjectName=f"{name}.Data",
                 LickStateSubjectName="LickState" + name.removeprefix("LicketySplit-"))


def camera(name: str) -> str:
    return piece(lint.CAMERA, Name=name, Board="BehaviorBoard-Main", BoardDataSubjectName="BehaviorBoard-Main.Data",
                 DataSubjectName=f"{name}.Data")


# The acceptance fixture: a rig no workflow has yet, a board clock, wheel and camera with DomeHardware's lick detectors
# and spout stage, made of the lab's pieces with no hardware code of its own.
MIXED = (include(lint.READ_LAB_RIG) + piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead")
         + named(lint.BOARD, "BehaviorBoard-Main") + board_line(lint.CLOCK_FROM_BOARD)
         + board_line(lint.RUNNING_WHEEL).replace("</Expression>", "<WheelDistanceSubjectName>WheelDistanceCm</WheelDistanceSubjectName></Expression>")
         + lick("LicketySplit-Left") + lick("LicketySplit-Right")
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
    rig_list = include(lint.DEVICES + "DomeHardware.bonsai", "<DisplayDevice>Second</DisplayDevice>")
    wf = workflow(tmp_path, include(lint.READ_LAB_RIG) + rig_list)
    assert lint.hardware_problems(wf) == [] and lint.speaker_mixer_problems(wf) == []
    assert dict(lint.hardware(wf))[lint.PROJECTOR_DISPLAY]["DisplayDevice"] == "Second"
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
    gate = piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead")
    late = broken.replace(gate, "") + gate + named(lint.CLOCK_FROM_GENERATOR, "TimestampGeneratorGen3-Main")
    assert lint.hardware_problems(workflow(tmp_path, late)) == [
        "the workflow has 2 clocks, not one (ClockFromTimestampGenerator or ClockFromBoard)",
        "BehaviorBoard BehaviorBoard-Main comes before ReadyGate",
        "ClockFromBoard BehaviorBoard-Main comes before ReadyGate",
        "RunningWheel BehaviorBoard-Main comes before ReadyGate",
        "LicketySplit LicketySplit-Left comes before ReadyGate",
        "LicketySplit LicketySplit-Right comes before ReadyGate",
        "LickSpoutStage comes before ReadyGate",
        "SpinnakerCamera Camera-Overhead comes before ReadyGate",
        "CameraTriggers BehaviorBoard-Main comes before ReadyGate",
        "LicketySplit LicketySplit-Right's DataSubjectName is LicketySplit-Left.Data, not LicketySplit-Right.Data",
        "CameraTriggers' CamerasExpected is Camera-Face, not Camera-Overhead"] + [f"{d}'s Harp clock is not held to the clock's: include HarpClockAgrees with Device {d}"
        for d in ("BehaviorBoard-Main", "LicketySplit-Left", "LicketySplit-Right", "StepperDriver-A", "StepperDriver-B")]


def test_with_a_clock_device_each_harp_device_is_held_to_its_clock(tmp_path):
    # P1R-H1 (audit 22 Nov 2026): a follower off its clock-sync cable logs on its own time, so each needs HarpClockAgrees,
    # which Ready waits for by its Device; a check on a rig with no clock device, or for no device, is refused.
    def agrees(device):
        return piece(lint.HARP_CLOCK_AGREES, Device=device, DeviceDataSubjectName=device + ".Data")
    gate = piece(lint.READY_GATE, PieceStages="HarpClockSet")
    rig = include(lint.READ_LAB_RIG) + gate + named(lint.CLOCK_FROM_GENERATOR, "TimestampGeneratorGen3-Main") + named(lint.BOARD, "BehaviorBoard-Main")
    assert lint.hardware_problems(workflow(tmp_path, rig)) == [
        "BehaviorBoard-Main's Harp clock is not held to the clock's: include HarpClockAgrees with Device BehaviorBoard-Main"]
    held = rig.replace(gate, piece(lint.READY_GATE, PieceStages="HarpClockSet, BehaviorBoard-Main")) + agrees("BehaviorBoard-Main")
    assert lint.hardware_problems(workflow(tmp_path / "b", held)) == []
    assert lint.hardware_problems(workflow(tmp_path / "c", held + agrees("LicketySplit-Left"))) == [
        "HarpClockAgrees' Device LicketySplit-Left is not a Harp device piece's Name",
        "ReadyGate's PieceStages is HarpClockSet, BehaviorBoard-Main, not HarpClockSet, BehaviorBoard-Main, LicketySplit-Left"]
    assert "HarpClockAgrees holds a device to ClockFromTimestampGenerator's clock, and the workflow has none" in         lint.hardware_problems(workflow(tmp_path / "d", MIXED + agrees("BehaviorBoard-Main")))


def test_a_piece_s_kind_board_and_camera_are_its_own(tmp_path):
    # RigDevice finds an entry by its Instance in the piece's own group: a board named Camera-Main opens the board main.
    misnamed = MIXED.replace("BehaviorBoard-Main", "Camera-Main")
    assert "BehaviorBoard Camera-Main is not named BehaviorBoard-<Instance>" in lint.hardware_problems(workflow(tmp_path, misnamed))
    second = named(lint.BOARD, "BehaviorBoard-Second")
    live = piece(lint.CAMERA_LIVE_CONTROLS, Camera="Camera-Overhead", Board="BehaviorBoard-Second")
    assert lint.hardware_problems(workflow(tmp_path / "b", MIXED + second + live)) == [
        "CameraLiveControls' Board BehaviorBoard-Second is not Camera-Overhead's, BehaviorBoard-Main"]
    triggers = piece(lint.CAMERA_TRIGGERS, Board="BehaviorBoard-Main", BoardCommandsSubjectName="BehaviorBoard-Main.Commands",
                     BoardDataSubjectName="BehaviorBoard-Main.Data", CamerasExpected="Camera-Overhead")
    elsewhere = MIXED.replace(triggers, triggers.replace("BehaviorBoard-Main", "BehaviorBoard-Second")) + second
    assert lint.hardware_problems(workflow(tmp_path / "c", elsewhere)) == [
        "SpinnakerCamera Camera-Overhead's Board BehaviorBoard-Main is not CameraTriggers' Board BehaviorBoard-Second"]
    pulses = piece(lint.MANUAL_PULSES, Board="BehaviorBoard-Other", BoardCommandsSubjectName="BehaviorBoard-Main.Data")
    assert lint.hardware_problems(workflow(tmp_path / "d", MIXED + pulses)) == [
        "ManualPulses's Board BehaviorBoard-Other is not a BehaviorBoard piece's Name",
        "ManualPulses BehaviorBoard-Other's BoardCommandsSubjectName is BehaviorBoard-Main.Data, not BehaviorBoard-Other.Commands"]


def test_a_rig_list_given_a_property_it_does_not_expose_is_refused(tmp_path):
    wf = workflow(tmp_path, include(lint.READ_LAB_RIG) + include(lint.DEVICES + "BBoxHardware.bonsai", "<PieceStages>HarpClockSet</PieceStages>"))
    assert lint.hardware_problems(wf) == ["BBoxHardware is given PieceStages, which it does not expose (Binning, WorkflowStages)"]


def own_include(tmp_path: Path, nodes: str, inner: str, edges: str = "") -> lint.Workflow:
    own_file(tmp_path, "Extensions/Own.bonsai", inner, edges)
    return workflow(tmp_path, nodes + include("Extensions\\Own.bonsai"))


def own_file(tmp_path: Path, name: str, inner: str, edges: str = "") -> None:
    """src/<name>, a workflow file of the workflow's own, written before the Workflow, which reads it."""
    (tmp_path / "src" / name).parent.mkdir(parents=True, exist_ok=True)
    (tmp_path / "src" / name).write_text(
        HEAD.replace("xmlns=", 'xmlns:orc="clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core" xmlns=') + inner
        + f"</Nodes><Edges>{edges}</Edges></Workflow></WorkflowBuilder>", encoding="utf-8")


def test_the_workflow_s_own_includes_are_the_workflow(tmp_path):
    # A piece or announcer in src\Extensions is the workflow's as much as one in main.bonsai.
    assert lint.hardware_problems(own_include(tmp_path, MIXED, camera("Camera-Side"))) == [
        "CameraTriggers' CamerasExpected is Camera-Overhead, not Camera-Overhead, Camera-Side",
        "ReadyGate's PieceStages is HarpClockSet, SpoutsHomed, Camera-Overhead, not HarpClockSet, SpoutsHomed, Camera-Overhead, Camera-Side"]
    assert ("Camera-Side", "camera") in lint.required_devices(own_include(tmp_path / "b", MIXED, camera("Camera-Side")))
    taken = ('<Expression xsi:type="Combinator"><Combinator xsi:type="rx:Take"><rx:Count>1</rx:Count></Combinator></Expression>'
             '<Expression xsi:type="MulticastSubject"><Name>ReadyStages</Name></Expression>')
    assert lint.hardware_problems(own_include(tmp_path / "c", MIXED, taken, '<Edge From="0" To="1" Label="Source1" />')) == [
        "ReadyStages is fed other than by one AnnounceStage"]
    stage = ('<Expression xsi:type="Combinator"><Combinator xsi:type="orc:AnnounceStage"><orc:Stage>Own</orc:Stage></Combinator></Expression>'
             '<Expression xsi:type="MulticastSubject"><Name>ReadyStages</Name></Expression>')
    assert lint.hardware_problems(own_include(tmp_path / "d", MIXED, stage, '<Edge From="0" To="1" Label="Source1" />')) == [
        "ReadyGate's WorkflowStages is not set, not the stages the workflow announces itself (Own)"]


def test_a_piece_behind_a_condition_is_driven_but_not_waited_for(tmp_path):
    # A calibration runs the stage only on a rig whose rig.yml has one (a SelectMany), so Ready cannot wait for SpoutsHomed.
    gate = piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead")
    behind = (MIXED.replace(include(lint.LICK_SPOUT_STAGE), "").replace(gate, piece(lint.READY_GATE, PieceStages="HarpClockSet, Camera-Overhead"))
              + '<Expression xsi:type="rx:SelectMany"><Name>Stage</Name><Workflow><Nodes>' + include(lint.LICK_SPOUT_STAGE)
              + "</Nodes><Edges /></Workflow></Expression>")
    wf = workflow(tmp_path, behind)
    assert lint.hardware_problems(wf) == []
    assert ("StepperDriver-A", "stepperDriver") in lint.required_devices(wf)


def test_a_board_clock_read_unset_waits_for_harp_clock_read_and_records_nothing(tmp_path):
    # A valve calibration on any rig: the board's time read, never set, and nothing recorded.
    gate = piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead")
    clock = board_line(lint.CLOCK_FROM_BOARD)
    unset = MIXED.replace(clock, clock.replace("</Expression>", "<SetClock>false</SetClock></Expression>"))
    read = unset.replace(gate, piece(lint.READY_GATE, PieceStages="HarpClockRead, SpoutsHomed, Camera-Overhead"))
    no_folder = include(lint.NO_EXPERIMENT_FOLDER)
    assert lint.hardware_problems(workflow(tmp_path, no_folder + read)) == []
    assert lint.hardware_problems(workflow(tmp_path / "b", no_folder + unset)) == [
        "ReadyGate's PieceStages is HarpClockSet, SpoutsHomed, Camera-Overhead, not HarpClockRead, SpoutsHomed, Camera-Overhead"]
    assert lint.hardware_problems(workflow(tmp_path / "c", read)) == [
        "ClockFromBoard's SetClock is false, so the board's clock is never set: only a workflow with NoExperimentFolder "
        "(one that records nothing) may read it unset"]
    # SetClock true is the default, as written by Bonsai.
    set_ = MIXED.replace(clock, clock.replace("</Expression>", "<SetClock>true</SetClock></Expression>"))
    assert lint.hardware_problems(workflow(tmp_path / "d", set_)) == []


def test_a_stage_the_workflow_announces_itself_is_one_of_ready_gate_s_workflow_stages(tmp_path):
    own = ('<Expression xsi:type="Combinator"><Combinator xmlns:orc="clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core" '
           'xsi:type="orc:AnnounceStage"><orc:Stage>CalibrationReady</orc:Stage></Combinator></Expression>')
    gate = piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead")
    listed = MIXED.replace(gate, piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead",
                                       WorkflowStages="CalibrationReady"))
    assert lint.hardware_problems(workflow(tmp_path, listed + own)) == []
    assert lint.hardware_problems(workflow(tmp_path / "b", MIXED + own)) == [
        "ReadyGate's WorkflowStages is not set, not the stages the workflow announces itself (CalibrationReady)"]
    assert lint.hardware_problems(workflow(tmp_path / "c", listed)) == [
        "ReadyGate's WorkflowStages is CalibrationReady, not the stages the workflow announces itself (none)"]


def test_a_recording_workflow_waits_for_the_lab_clock_at_start(tmp_path):
    # P1R-L4 (audit 22 Nov 2026): LabClockReady checks the clock at Start and announces LabClockChecked, so a clock
    # fault stops the workflow before Ready, not at Go; a rig list's PieceStages is fixed, so it is a WorkflowStage.
    logs, ready = include(lint.LOG_LAB_EXPERIMENT), include(lint.LAB_CLOCK_READY)
    once = "the workflow includes LabClockReady {} times: once, outside any per-element operator, with ExperimentLogs, else none"
    assert lint.hardware_problems(workflow(tmp_path, logs + MIXED)) == [once.format(0)]
    assert lint.hardware_problems(workflow(tmp_path / "b", logs + MIXED + ready)) == [
        "ReadyGate's WorkflowStages is not set, not the stages the workflow announces itself (LabClockChecked)"]
    gate = piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead")
    waits = MIXED.replace(gate, piece(lint.READY_GATE, PieceStages="HarpClockSet, SpoutsHomed, Camera-Overhead",
                                      WorkflowStages="LabClockChecked"))
    assert lint.hardware_problems(workflow(tmp_path / "c", logs + waits + ready)) == []
    # Before ReadyGate it would announce into a ReadyStages not yet built; without ExperimentLogs nothing is recorded.
    assert lint.hardware_problems(workflow(tmp_path / "d", logs + ready + waits)) == ["LabClockReady comes before ReadyGate"]
    assert lint.hardware_problems(workflow(tmp_path / "e", waits + ready)) == [once.format(1)]
    listed = include(lint.READ_LAB_RIG) + include(next(iter(lint.RIG_LISTS)), "<WorkflowStages>LabClockChecked</WorkflowStages>")
    assert lint.hardware_problems(workflow(tmp_path / "f", logs + listed + ready)) == []


CALIBRATION_READY = ('<Expression xsi:type="Combinator"><Combinator xsi:type="orc:AnnounceStage"><orc:Stage>CalibrationReady</orc:Stage>'
                     '</Combinator></Expression><Expression xsi:type="MulticastSubject"><Name>ReadyStages</Name></Expression>')
ANNOUNCED = '<Edge From="0" To="1" Label="Source1" />'
LIVE_VIEW = '<Expression xsi:type="lv:StreamDevices" xmlns:lv="clr-namespace:OpenRigs.LiveView;assembly=OpenRigs.LiveView" />'


def thin_entry(tmp_path: Path, rig_list: str, settings: str = "") -> lint.Workflow:
    """The rule: one thin entry per rig kind (its rig list) over one shared include of the workflow's
    own, which holds the job and the live view; the job's spouts, a file beside it, announce CalibrationReady."""
    own_file(tmp_path, "Extensions/Spouts.bonsai", CALIBRATION_READY, ANNOUNCED)
    own_file(tmp_path, "Extensions/Job.bonsai", include("Extensions\\Spouts.bonsai") + LIVE_VIEW)
    return workflow(tmp_path, include(lint.READ_LAB_RIG) + include(lint.DEVICES + rig_list, settings) + include("Extensions\\Job.bonsai"))


def test_a_thin_entry_s_rig_list_waits_for_the_stage_its_shared_include_announces(tmp_path):
    for n, rig_list in enumerate(lint.RIG_LISTS):
        name = rig_list.split(":")[1]
        wf = thin_entry(tmp_path / str(n), name, "<WorkflowStages>CalibrationReady</WorkflowStages>")
        assert lint.hardware_problems(wf) == []
        assert dict(lint.hardware(wf))[lint.READY_GATE]["WorkflowStages"] == "CalibrationReady"
        assert lint.live_view_problems(wf) == []
        assert lint.hardware_problems(thin_entry(tmp_path / f"{n}b", name)) == [
            "ReadyGate's WorkflowStages is not set, not the stages the workflow announces itself (CalibrationReady)"]


def test_an_own_include_is_opened_from_src_as_bonsai_opens_it_and_one_bonsai_would_not_find_stops_the_lint(tmp_path):
    # Bonsai 2.9 opens an include's Path from its working directory, the launched workflow's folder (src), never from
    # the including file's: Extensions\Job.bonsai's Extensions\Spouts.bonsai is src\Extensions\Spouts.bonsai.
    own_file(tmp_path, "Extensions/Extensions/Spouts.bonsai", CALIBRATION_READY, ANNOUNCED)
    own_file(tmp_path, "Extensions/Job.bonsai", include("Extensions\\Spouts.bonsai"))
    with pytest.raises(ValueError, match=r"src\\Extensions\\Job\.bonsai includes Extensions\\Spouts\.bonsai, which Bonsai opens as "
                                         r".*src[\\/]Extensions[\\/]Spouts\.bonsai \(from src"):
        workflow(tmp_path, include("Extensions\\Job.bonsai"))
    # A Path with no extension is opened with .bonsai added.
    own_file(tmp_path / "b", "Extensions/Spouts.bonsai", CALIBRATION_READY, ANNOUNCED)
    wf = workflow(tmp_path / "b", include(lint.READ_LAB_RIG) + piece(lint.READY_GATE, PieceStages="", WorkflowStages="CalibrationReady")
                  + include("Extensions\\Spouts"))
    assert "<orc:Stage>CalibrationReady</orc:Stage>" in wf.text and len(wf.texts) == 2
    own_file(tmp_path / "c", "Loop.bonsai", include("Loop.bonsai"))
    with pytest.raises(ValueError, match=r"src\\Loop\.bonsai's include Loop\.bonsai includes itself"):
        workflow(tmp_path / "c", include("Loop.bonsai"))


def test_experiment_control_in_an_own_include_is_given_the_launch_id_through_it(tmp_path):
    mapping = '<Expression xsi:type="ExternalizedMapping"><Property Name="LaunchId" /></Expression>'
    own_file(tmp_path, "Extensions/Initialization.bonsai", mapping + include(lint.EXPERIMENT_CONTROL), ANNOUNCED)
    (tmp_path / "src" / "main.bonsai").write_text(HEAD + mapping + include("Extensions\\Initialization.bonsai")
                                                  + f"</Nodes><Edges>{ANNOUNCED}</Edges></Workflow></WorkflowBuilder>", encoding="utf-8")
    assert lint.launch_id_problems(lint.Workflow(tmp_path)) == []
    assert lint.launch_id_problems(workflow(tmp_path, include("Extensions\\Initialization.bonsai"))) == [
        "the include Extensions\\Initialization.bonsai is not given LaunchId by an ExternalizedMapping (map the top-level LaunchId down to ExperimentControl)"]
    own_file(tmp_path / "b", "Extensions/Initialization.bonsai", include(lint.EXPERIMENT_CONTROL))
    (tmp_path / "b" / "src" / "main.bonsai").write_text(HEAD + mapping + include("Extensions\\Initialization.bonsai")
                                                        + f"</Nodes><Edges>{ANNOUNCED}</Edges></Workflow></WorkflowBuilder>", encoding="utf-8")
    assert lint.launch_id_problems(lint.Workflow(tmp_path / "b")) == [
        "ExperimentControl is not given LaunchId by an ExternalizedMapping (map the top-level LaunchId down to ExperimentControl)"]


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


def test_a_wall_clock_wait_in_an_allowed_group_is_left_out(tmp_path):
    timer = '<Expression xsi:type="Combinator"><Combinator xsi:type="rx:Timer" /></Expression>'
    backstop = f'<Expression xsi:type="GroupWorkflow"><Name>Backstop</Name><Workflow><Nodes>{timer}</Nodes><Edges /></Workflow></Expression>'
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "main.bonsai").write_text(
        HEAD + f'<Expression xsi:type="GroupWorkflow"><Name>TaskLogic</Name><Workflow><Nodes>{timer}{backstop}</Nodes><Edges /></Workflow></Expression>'
        "</Nodes><Edges /></Workflow></WorkflowBuilder>", encoding="utf-8")
    wf = lint.Workflow(tmp_path)
    assert lint.wall_clock_waits(wf) == ["rx:Timer (rx:Timer)", "rx:Timer (rx:Timer)"]
    assert lint.wall_clock_waits(wf, allowed=["Backstop"]) == ["rx:Timer (rx:Timer)"]


def test_a_command_the_workflow_handles_and_does_not_declare_is_found(tmp_path):
    on = '<Expression xsi:type="Combinator"><Combinator xsi:type="c:OnCommand"><c:Name>{}</c:Name></Combinator></Expression>'
    wf = workflow(tmp_path, '<Expression xsi:type="GroupWorkflow" xmlns:c="clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control">'
                  '<Name>Commands</Name><Workflow><Nodes>' + on.format("RewardLeft") + on.format("Flush")
                  + "</Nodes><Edges /></Workflow></Expression>" + include(lint.EXPERIMENT_CONTROL, "<Commands><Command><Name>RewardLeft</Name></Command></Commands>"))
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
    send = '<Expression xsi:type="MulticastSubject"><Name>{}</Name></Expression>'
    edge = '<Edge From="0" To="1" Label="Source1" />'
    valves = {"DO1", "DO2", "DOPort0"}
    written = ('<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreateOutputSetPayload"><beh:OutputSet>DO1</beh:OutputSet></harp:Payload></Expression>'
               '<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreatePulseDO2Payload"><beh:PulseDO2>10</beh:PulseDO2></harp:Payload></Expression>'
               '<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreateOutputSetPayload"><beh:OutputSet>DO3</beh:OutputSet></harp:Payload></Expression>'
               '<Expression xsi:type="beh:CreateMessage"><harp:MessageType>Write</harp:MessageType>'
               '<harp:Payload xsi:type="beh:CreatePulseDOPort0Payload"><beh:PulseDOPort0>10</beh:PulseDOPort0></harp:Payload></Expression>')
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "d", written, ""), valves) == [
        "CreateOutputSetPayload writes DO1, a RewardValve, itself", "CreatePulseDO2Payload writes DO2, a RewardValve, itself",
        "CreatePulseDOPort0Payload writes DOPort0, a RewardValve, itself"]
    calibrated = ('<Expression xsi:type="Combinator"><Combinator xsi:type="orc:FirePulses" /></Expression>'
                  + send.format("BehaviorBoard-Main.TriggerCalibratedPulse"))
    assert lint.fixed_width_pulses_on_reward_valves(pulse_workflow(tmp_path / "e", calibrated, edge), valves) == []


MF = 'xmlns:mf="clr-namespace:OpenRigs.Manifest;assembly=OpenRigs.Manifest"'


def manifest_node(expected: str) -> str:
    return f'<Expression xsi:type="mf:ExperimentManifest" {MF}><mf:ExpectedFiles>{expected}</mf:ExpectedFiles></Expression>'


LOGS = (include("OpenRigs.Logging:LogTrials.bonsai") + include("OpenRigs.Logging:LogEvents.bonsai")
        + include("OpenRigs.Logging:LogDataSchema.bonsai", "<Name>Settings_Rig</Name>")
        + include("OpenRigs.Logging:LogWorkflowData.bonsai", "<LogName>WorkflowData_StimulusState</LogName>")
        + include(lint.ZAPIT_FOR_TASK) + include("OpenRigs.Devices:BehaviorBoard.bonsai")
        + '<Expression xsi:type="hd:RecordHarpDevices" xmlns:hd="clr-namespace:OpenRigs.HarpDevices;assembly=OpenRigs.HarpDevices" />')
EXPECTED = ("auto;harp;Settings/Settings_Rig_;WorkflowData/WorkflowData_Trials_;WorkflowData/WorkflowData_Events_;WorkflowData/WorkflowData_StimulusState_;"
            "Camera-Zapit/?Optogenetics;ZapitScanner/?Optogenetics;Settings/Settings_ZapitBank_?Optogenetics;Settings/Settings_ZapitSample_?Optogenetics;Settings/Settings_ZapitScannerFit_?Optogenetics;Settings/Settings_ZapitAccuracy_?Optogenetics;Settings/Settings_ZapitConditions_?Optogenetics")


def test_every_file_a_workflow_writes_is_named_in_expected_files_with_its_condition(tmp_path):
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(EXPECTED))) == []
    # LabClockReady checks the clock and writes nothing (P1R-L4).
    assert lint.expected_files_problems(workflow(tmp_path / "b", LOGS + include(lint.LAB_CLOCK_READY) + manifest_node(EXPECTED))) == []


def test_a_folder_entry_in_place_of_each_log_is_refused(tmp_path):
    folder = EXPECTED.replace("WorkflowData/WorkflowData_Trials_;WorkflowData/WorkflowData_Events_;", "WorkflowData/;")
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(folder))) == [
        "WorkflowData/WorkflowData_Trials_ is written but not in ExpectedFiles (name it, not WorkflowData/)",
        "WorkflowData/WorkflowData_Events_ is written but not in ExpectedFiles (name it, not WorkflowData/)",
        "ExpectedFiles names WorkflowData/, which no node in the workflow writes (name each log instead)"]


def test_a_log_written_under_a_condition_must_be_named_with_it(tmp_path):
    unconditional = EXPECTED.replace("Settings/Settings_ZapitBank_?Optogenetics", "Settings/Settings_ZapitBank_")
    assert lint.expected_files_problems(workflow(tmp_path, LOGS + manifest_node(unconditional))) == [
        "ExpectedFiles names Settings/Settings_ZapitBank_; its block writes it as Settings/Settings_ZapitBank_?Optogenetics"]


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


def block_text(path: str) -> str:
    package, resource = path.split(":")
    return (ROOT / "src" / package / resource.replace(".", "/", resource.count(".") - 1)).read_text(encoding="utf-8-sig")


def test_the_fixed_logs_are_the_names_their_blocks_write():
    for path, logs in lint.FIXED_LOGS.items():
        text = block_text(path)
        for inner in (lint.TASK_LOGS, "OpenRigs.Logging:LogEvents.bonsai", "OpenRigs.Logging:LogTrials.bonsai"):
            if f'Path="{inner}"' in text:  # a block that includes another writes its logs too
                text += block_text(inner)
        assert all(f">{name}<" in text for name, _ in logs), path


def task_logic(tmp_path: Path, nodes: str) -> lint.Workflow:
    return workflow(tmp_path, f'<Expression xsi:type="GroupWorkflow"><Name>TaskLogic</Name><Workflow><Nodes>{nodes}</Nodes><Edges /></Workflow></Expression>')


def test_the_rng_seed_event_is_the_lab_s_include_at_task_logic_s_top_level(tmp_path):
    assert lint.rng_seed_problems(task_logic(tmp_path, include(lint.LOG_RNG_SEED))) == []
    # A task's own chain, refused: a CreateEvent of RngSeed into Events.
    own = ('<Expression xsi:type="Combinator"><Combinator xsi:type="orl:CreateEvent" '
           'xmlns:orl="clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging"><orl:Event>RngSeed</orl:Event></Combinator></Expression>')
    assert lint.rng_seed_problems(task_logic(tmp_path / "b", own)) == [
        "LogRngSeed is included 0 times, 0 at TaskLogic's top level; it is once, there", "the workflow makes its own RngSeed event (LogRngSeed does)"]
    text = block_text(lint.LOG_RNG_SEED)
    assert "<orl:Event>RngSeed</orl:Event>" in text and text.index("<Name>Events</Name>") < text.index("rx:Take")


def test_the_task_logs_are_the_lab_s_include_given_the_task_file_as_json(tmp_path):
    serialize = '<Expression xsi:type="Combinator"><Combinator xsi:type="t:SerializeToJson" xmlns:t="clr-namespace:Task;assembly=Extensions" /></Expression>'
    edge = '</Nodes><Edges><Edge From="0" To="1" Label="Source1" /></Edges></Workflow></Expression>'
    group = '<Expression xsi:type="GroupWorkflow"><Workflow><Nodes>'
    assert lint.task_logs_problems(workflow(tmp_path, group + serialize + include(lint.TASK_LOGS) + edge)) == []
    # A task's own logs, refused.
    own = (include("OpenRigs.Logging:LogTrials.bonsai") + include("OpenRigs.Logging:LogDataSchema.bonsai", "<Name>Settings_Task</Name>")
           )
    assert lint.task_logs_problems(workflow(tmp_path / "b", own)) == [
        "the workflow includes TaskLogs 0 times, not once", "the workflow includes LogTrials itself (TaskLogs does)",
        "the workflow logs Settings_Task itself (TaskLogs does)"]
    assert lint.task_logs_problems(workflow(tmp_path / "c", include(lint.TASK_LOGS))) == [
        "TaskLogs is not given the task file as JSON (the task's SerializeToJson)"]
    expected = "Settings/Settings_Task_;WorkflowData/WorkflowData_Trials_"
    assert lint.expected_files_problems(workflow(tmp_path / "d", include(lint.TASK_LOGS) + manifest_node(expected))) == []


def test_conditioned_task_logs_count_as_task_logs_and_write_settings_combinations(tmp_path):
    serialize = '<Expression xsi:type="Combinator"><Combinator xsi:type="t:SerializeToJson" xmlns:t="clr-namespace:Task;assembly=Extensions" /></Expression>'
    edge = '</Nodes><Edges><Edge From="0" To="1" Label="Source1" /></Edges></Workflow></Expression>'
    group = '<Expression xsi:type="GroupWorkflow"><Workflow><Nodes>'
    assert lint.task_logs_problems(workflow(tmp_path, group + serialize + include(lint.CONDITIONED_TASK_LOGS) + edge)) == []
    # Beside TaskLogs, or with the workflow's own Settings_Combinations writer, refused.
    both = include(lint.CONDITIONED_TASK_LOGS) + include(lint.TASK_LOGS) + include("OpenRigs.Logging:LogDataSchema.bonsai", "<Name>Settings_Combinations</Name>")
    assert lint.task_logs_problems(workflow(tmp_path / "b", both)) == [
        "the workflow includes TaskLogs 2 times, not once",
        "the workflow logs Settings_Combinations itself (ConditionedTaskLogs does)",
        "ConditionedTaskLogs is not given the task file as JSON (the task's SerializeToJson)",
        "TaskLogs is not given the task file as JSON (the task's SerializeToJson)"]
    expected = "Settings/Settings_Task_;WorkflowData/WorkflowData_Trials_"
    assert lint.expected_files_problems(workflow(tmp_path / "c", include(lint.CONDITIONED_TASK_LOGS) + manifest_node(expected))) == [
        "Settings/Settings_Combinations_ is written but not in ExpectedFiles"]
    expected += ";Settings/Settings_Combinations_"
    assert lint.expected_files_problems(workflow(tmp_path / "d", include(lint.CONDITIONED_TASK_LOGS) + manifest_node(expected))) == []
    # Known by name: the embedded piece writes exactly these.
    text = block_text(lint.CONDITIONED_TASK_LOGS)
    assert f'Path="{lint.TASK_LOGS}"' in text and "<Name>Settings_Combinations</Name>" in text


def test_no_include_ends_its_logs_when_a_one_shot_write_completes():
    for path in sorted((ROOT / "src").glob("OpenRigs.*/**/*.bonsai")):
        if not path.relative_to(ROOT / "src").parts[0].endswith(".Tests"):
            assert lint.output_ends_logs_problems(lint.ET.fromstring(path.read_bytes())) == [], path
    # A WorkflowOutput fed by Settings_Task, which completes at Go.
    old = block_text(lint.TASK_LOGS).replace('<Edge From="0" To="1" Label="Source1" />',
                                            '<Edge From="0" To="1" Label="Source1" /><Edge From="1" To="9" Label="Source1" />')
    old = old.replace('\n    </Nodes>', '\n      <Expression xsi:type="WorkflowOutput" />\n    </Nodes>')
    assert lint.output_ends_logs_problems(lint.ET.fromstring(old.encode("utf-8"))) == [
        f"{name} logs beside a WorkflowOutput fed by a one-shot LogDataSchema, which ends it when the write completes"
        for name in ("TrialLogs",)]


def test_the_penalty_sound_is_the_lab_s_include(tmp_path):
    assert rules.play_penalty_sound_problems(lint.ET.fromstring(block_text(lint.PLAY_PENALTY_SOUND).encode("utf-8"))) == []
    assert lint.penalty_sound_problems(workflow(tmp_path, include(lint.PLAY_PENALTY_SOUND))) == []
    # TrialOutcomeEffects includes it: counted, and a second of either is one too many.
    assert lint.penalty_sound_problems(workflow(tmp_path / "effects", include(lint.TRIAL_OUTCOME_EFFECTS))) == []
    assert lint.penalty_sound_problems(workflow(tmp_path / "both", include(lint.TRIAL_OUTCOME_EFFECTS) + include(lint.PLAY_PENALTY_SOUND))) == [
        "the workflow includes PlayPenaltySound 2 times, not once"]
    text = block_text(lint.PLAY_PENALTY_SOUND)
    unscaled = text.replace('xsi:type="aud:SoundAtLevel"', 'xsi:type="aud:SpeakerBuffer"')
    assert rules.play_penalty_sound_problems(lint.ET.fromstring(unscaled.encode("utf-8"))) == [
        "PlayPenaltySound does not play each input at its level from the buffer's (SoundAtLevel, BufferLevelDbfs from MaxPenaltyLevelDbfs)"]
    at_go = text.replace("<Name>TrialRules</Name>", "<Name>TaskConfig</Name>")
    assert rules.play_penalty_sound_problems(lint.ET.fromstring(at_go.encode("utf-8"))) == [
        "the penalty sound is not laid out at TrialRules' MaxPenaltyLevelDbfs"]
    # A task's own chain, refused.
    own = ('<Expression xsi:type="Combinator"><Combinator xsi:type="aud:PenaltySoundBuffer" xmlns:aud="clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio" /></Expression>'
           '<Expression xsi:type="MulticastSubject"><Name>PenaltySounds</Name></Expression>')
    assert lint.penalty_sound_problems(workflow(tmp_path / "b", own)) == [
        "the workflow includes PlayPenaltySound 0 times, not once", "the workflow makes its own PenaltySoundBuffer (PlayPenaltySound does)",
        "the workflow sends PenaltySounds itself (PlayPenaltySound does)"]


def test_the_zapit_trigger_line_is_set_and_cleared_by_the_lab_s_include(tmp_path):
    board = named(lint.BOARD, "BehaviorBoard-Main") + include(lint.ZAPIT_FOR_TASK)
    line = include(lint.SET_ZAPIT_TRIGGER_LINE, "<Set>true</Set><BoardCommandsSubjectName>BehaviorBoard-Main.Commands</BoardCommandsSubjectName>")
    assert lint.zapit_include_problems(workflow(tmp_path, board + line)) == []
    # A chain of its own, an Optogenetics flag of its own, and a write sent anywhere but a board's commands.
    own = ('<Expression xsi:type="Combinator"><Combinator xsi:type="zap:ZapitLineMessage" xmlns:zap="clr-namespace:OpenRigs.Devices.Zapit;assembly=OpenRigs.Devices" /></Expression>'
           '<Expression xsi:type="rx:AsyncSubject"><Name>Optogenetics</Name></Expression>' + include(lint.SET_ZAPIT_TRIGGER_LINE)
           + line.replace("BehaviorBoard-Main.Commands", "BehaviorBoard-Main.Data"))
    assert lint.zapit_include_problems(workflow(tmp_path / "b", board + own)) == [
        "the workflow declares Optogenetics itself (ConnectForTask does)",
        "the workflow writes the ZapitTrigger line itself (SetZapitTriggerLine does)",
        "a SetZapitTriggerLine does not state Set",
        "a SetZapitTriggerLine's BoardCommandsSubjectName is not set, not a BehaviorBoard piece's <Name>.Commands (BehaviorBoard-Main.Commands)",
        "a SetZapitTriggerLine's BoardCommandsSubjectName is BehaviorBoard-Main.Data, not a BehaviorBoard piece's <Name>.Commands (BehaviorBoard-Main.Commands)"]
    text = block_text(lint.SET_ZAPIT_TRIGGER_LINE)
    assert 'DisplayName="BoardCommandsSubjectName"' in text and "<Name>ZapitTriggerLine</Name>" in text


def test_the_behaviour_box_rig_list_is_expanded_with_the_workflow_s_binning(tmp_path):
    wf = workflow(tmp_path, include(lint.READ_LAB_RIG) + include(lint.DEVICES + "BBoxHardware.bonsai", "<Binning>2</Binning>"))
    assert lint.hardware_problems(wf) == []
    assert lint.required_devices(wf) == [("BehaviorBoard-Main", "behaviorBoard"), ("Camera-Overhead", "camera")]
    assert dict(lint.hardware(wf))[lint.CAMERA]["Binning"] == "2"


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
    wf = workflow(tmp_path, include(lint.DEVICES + "DomeHardware.bonsai"))
    assert len(lint.required_devices(wf)) == 9

def test_zapit_and_the_manual_rewards_are_the_lab_s_includes(tmp_path):
    def root(resource: str) -> lint.ET.Element:
        return lint.ET.fromstring((ROOT / "src" / "OpenRigs.Devices" / resource).read_bytes())
    assert rules.zapit_for_task_problems(root("ConnectForTask.bonsai")) == []
    assert rules.manual_rewards_drop_problems(root("RewardValve.bonsai")) == []
    group = '<Expression xsi:type="GroupWorkflow"><Name>Commands</Name><Workflow><Nodes /><Edges /></Workflow></Expression>'
    wf = workflow(tmp_path, include(lint.ZAPIT_FOR_TASK) + include(lint.REWARDS) + VALVES + group)
    # Each RewardValve declares its own manual drop (DeclareDeviceControls, RewardValve-Left.Reward): the task declares none.
    assert lint.zapit_include_problems(wf) == [] and lint.reward_problems(wf) == [] and lint.undeclared_commands(wf) == []
    assert {name for _, kind, name in lint.control_declarations(wf) if kind == "command"} == {"RewardValve-Left.Reward", "RewardValve-Right.Reward"}
    copy = workflow(tmp_path / "b", include(lint.REWARDS) + VALVES + include(lint.EXPERIMENT_CONTROL, "<Commands><Command><Name>RewardValve-Left.Reward</Name></Command></Commands>"))
    assert lint.declared_twice(copy) == ["the command RewardValve-Left.Reward is declared by ExperimentControl and by RewardValve-Left"]
    # ConnectForTask declares Zapit's subjects: a task's copy of one is refused, and so is every legacy Zapit block and node.
    events = '<Expression xsi:type="rx:PublishSubject"><rx:Name>Zapit.Events</rx:Name></Expression>'
    legacy = (include("OpenRigs.Devices:ConnectZapit.bonsai") + include("OpenRigs.Devices:ZapitLogs.bonsai") + include(lint.ZAPIT_SETUP)
              + '<Expression xsi:type="Combinator"><Combinator xsi:type="zap:ZapitClient" xmlns:zap="clr-namespace:OpenRigs.Devices.Zapit;assembly=OpenRigs.Devices" /></Expression>'
              + '<Expression xsi:type="Combinator"><Combinator xsi:type="z:ZapitDaq" xmlns:z="clr-namespace:OpenRigs.Zapit;assembly=OpenRigs.Zapit" /></Expression>')
    assert lint.zapit_include_problems(workflow(tmp_path / "c", include(lint.ZAPIT_FOR_TASK) + events + legacy)) == [
        "the workflow includes ConnectZapit, a legacy Zapit block: ConnectForTask and TaskLogs replace it",
        "the workflow includes ZapitLogs, a legacy Zapit block: ConnectForTask and TaskLogs replace it",
        "the workflow includes ZapitSetup itself (ConnectForTask does)",
        "the workflow builds zap:ZapitClient, a legacy Zapit node: ConnectForTask and ZapitTrials replace it",
        "the workflow builds ozap:ZapitDaq itself (ConnectForTask does)",
        "the workflow declares Zapit.Events, which ConnectForTask declares"]


def test_the_shared_experiment_logs_are_the_lab_s_two_includes(tmp_path):
    both = include("OpenRigs.Logging:ExperimentLogs.bonsai") + include("OpenRigs.Logging:LogControlEvents.bonsai")
    assert lint.experiment_log_problems(workflow(tmp_path, both)) == []
    own = include("OpenRigs.Logging:LogController.bonsai") + include("OpenRigs.Logging:LogDataSchema.bonsai", "<Name>Settings_Rig</Name>")
    assert lint.experiment_log_problems(workflow(tmp_path / "b", own)) == [
        "the workflow does not include ExperimentLogs once", "the workflow does not include LogControlEvents once",
        "the workflow includes LogController itself (ExperimentLogs does)", "the workflow logs Settings_Rig itself (ExperimentLogs does)"]
    # ExperimentLogs is its own top-level node, before the hardware (every device's log waits for its PathPrefix).
    gate = include(lint.READY_GATE)
    assert lint.experiment_log_problems(workflow(tmp_path / "d", gate + both)) == ["ExperimentLogs comes after the hardware"]
    grouped = f'<Expression xsi:type="GroupWorkflow"><Workflow><Nodes>{both}</Nodes><Edges /></Workflow></Expression>'
    assert lint.experiment_log_problems(workflow(tmp_path / "e", grouped + gate)) == ["ExperimentLogs is not a top-level node of its own"]
    assert lint.experiment_log_problems(workflow(tmp_path / "f", both + gate)) == []
    assert lint.expected_files_problems(workflow(tmp_path / "c", both + manifest_node("WorkflowData/WorkflowData_Events_;Settings/Settings_Rig_;Settings/Settings_Experiment_"))) == []


CONTROL = 'xmlns:orcl="clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control"'
STEP_SIZE = "<Parameters><Parameter><Name>SpoutStepSize</Name></Parameter></Parameters>"


def test_the_spout_step_size_is_the_lick_spout_stage_s_and_the_workflow_declares_none_of_it(tmp_path):
    stage = include(lint.READ_LAB_RIG) + include(lint.READY_GATE) + include(lint.LICK_SPOUT_STAGE)
    assert lint.spout_step_size_problems(workflow(tmp_path, stage + include(lint.EXPERIMENT_CONTROL))) == []
    # A workflow's own declaration, subject and live set, mapped onto the rig list.
    own = (include(lint.EXPERIMENT_CONTROL, STEP_SIZE) + '<Expression xsi:type="rx:BehaviorSubject"><Name>SpoutStepSize</Name></Expression>'
           f'<Expression xsi:type="Combinator"><Combinator xsi:type="orcl:OnParameterSet" {CONTROL}><orcl:Name>SpoutStepSize</orcl:Name></Combinator></Expression>'
           '<Expression xsi:type="PropertyMapping"><PropertyMappings><Property Name="SpoutStepSize" /></PropertyMappings></Expression>')
    wf = workflow(tmp_path / "c", include(lint.READ_LAB_RIG) + include(lint.DEVICES + "DomeHardware.bonsai") + own)
    assert lint.spout_step_size_problems(wf) == [
        "the workflow handles SpoutStepSize itself (LickSpoutStage does)", "the workflow sets SpoutStepSize on an include, which takes it from the parameter"]
    assert lint.declared_twice(wf) == ["the parameter SpoutStepSize is declared by ExperimentControl and by LickSpoutStage"]
    assert lint.spout_step_size_problems(workflow(tmp_path / "d", include(lint.READ_LAB_RIG))) == []


def test_a_task_without_its_pieces_copies_passes_and_one_with_them_does_not(tmp_path, monkeypatch):
    # As a workflow repo reads them: from the OpenRigs.Devices package Bonsai.config pins, the rig list's pieces inside it.
    dll = b"MZ"
    for resource in ("DomeHardware.bonsai", "LickSpoutStage.bonsai", "RewardValve.bonsai"):
        dll += b"\x00\x10" + (ROOT / "src" / "OpenRigs.Devices" / resource).read_bytes()
    (tmp_path / ".bonsai").mkdir()
    (tmp_path / ".bonsai" / "Bonsai.config").write_text('<Packages><Package id="OpenRigs.Devices" version="0.1.1-x.2" /></Packages>')
    (tmp_path / "local_packages").mkdir()
    with zipfile.ZipFile(tmp_path / "local_packages" / "OpenRigs.Devices.0.1.1-x.2.nupkg", "w") as z:
        z.writestr("lib/net472/OpenRigs.Devices.dll", dll + b"\x00")
    monkeypatch.setattr(lint, "LIB_SRC", tmp_path / "nowhere")
    pieces = include(lint.READ_LAB_RIG) + include(lint.DEVICES + "DomeHardware.bonsai") + VALVES
    task = "<Parameters><Parameter><Name>Speed</Name></Parameter></Parameters>"
    without = workflow(tmp_path, pieces + include(lint.EXPERIMENT_CONTROL, task))
    assert lint.undeclared_commands(without) == [] and lint.declared_twice(without) == [] and lint.spout_step_size_problems(without) == []
    assert {(who, name) for who, kind, name in lint.control_declarations(without) if kind == "command"} >= {("LickSpoutStage", "SpoutsHome"), ("RewardValve-Right", "RewardValve-Right.Reward")}
    (tmp_path / "src" / "main.bonsai").unlink()
    (tmp_path / "src").rmdir()
    copies = "<Commands><Command><Name>SpoutsHome</Name></Command></Commands>"
    with_copies = workflow(tmp_path, pieces + include(lint.EXPERIMENT_CONTROL, task + copies))
    assert lint.declared_twice(with_copies) == ["the command SpoutsHome is declared by ExperimentControl and by LickSpoutStage"]


def test_every_declaring_piece_is_readable_from_the_package():
    # included_roots reads a lab include from a workflow repo's package only by its signature: one that declares
    # controls and has none would leave its declarations unread.
    for path in (ROOT / "src").glob("OpenRigs.*/**/*.bonsai"):
        if "Tests" not in path.parts[-2] and "DeclareControls" in path.read_text(encoding="utf-8-sig"):
            relative = path.relative_to(ROOT / "src")
            assert f"{relative.parts[0]}:{'.'.join(relative.parts[1:])}" in lint.SIGNATURES, path


def test_the_lick_spout_stage_takes_the_spout_step_size_live_and_shows_its_default():
    text = (ROOT / "src" / "OpenRigs.Devices" / "LickSpoutStage.bonsai").read_text(encoding="utf-8-sig")
    root = lint.ET.fromstring(text.encode("utf-8"))
    names = {(c.get(lint.XSI), c.findtext(lint.CONTROL + "Name")) for c in root.iter(lint.W + "Combinator")}
    assert {("orcl:OnParameterSet", "SpoutStepSize"), ("orcl:ParameterDefault", "SpoutStepSize")} <= names
    assert text.count("<Value>150</Value>") == 1


def task_files(nodes: str = "") -> str:
    return ('<Expression xsi:type="rx:AsyncSubject"><Name>TaskConfig</Name></Expression>'
            '<Expression xsi:type="rx:AsyncSubject"><Name>LaunchTaskConfig</Name></Expression>' + nodes)


def test_every_task_with_sound_has_the_lab_s_background_noise_once_after_its_task_files(tmp_path):
    rig = include(lint.READ_LAB_RIG) + include(lint.DEVICES + "DomeHardware.bonsai")
    noise = include(lint.BACKGROUND_NOISE)
    assert lint.background_noise_problems(workflow(tmp_path, rig + task_files() + noise)) == []
    # The noise is rig.yml's speakerArray.backgroundNoise: a file named on the include, or a noise WAV kept in the task, is refused.
    own_file = workflow(tmp_path / "f", rig + task_files() + include(lint.BACKGROUND_NOISE, "<FileName>waveforms\\background_noise.wav</FileName>"))
    (tmp_path / "f" / "src" / "waveforms").mkdir()
    (tmp_path / "f" / "src" / "waveforms" / "Background_Noise.wav").write_bytes(b"RIFF")
    (tmp_path / "f" / "src" / "waveforms" / "tone.wav").write_bytes(b"RIFF")
    assert lint.background_noise_problems(own_file) == [
        "the workflow gives BackgroundNoise a FileName (the noise is rig.yml's speakerArray.backgroundNoise)",
        "src/waveforms/Background_Noise.wav is a noise of the task's own (BackgroundNoise plays rig.yml's speakerArray.backgroundNoise)"]
    assert lint.background_noise_problems(workflow(tmp_path / "b", rig + task_files())) == ["the workflow includes BackgroundNoise 0 times, not once"]
    assert lint.background_noise_problems(workflow(tmp_path / "c", rig + noise + task_files())) == []  # it reads no task file
    own = '<Expression xsi:type="MulticastSubject"><Name>BackgroundNoise</Name></Expression>'
    assert lint.background_noise_problems(workflow(tmp_path / "d", rig + task_files(own) + noise)) == [
        "the workflow sends BackgroundNoise itself (the BackgroundNoise include does)"]
    assert lint.background_noise_problems(workflow(tmp_path / "e", include(lint.READ_LAB_RIG) + noise)) == [
        "the workflow includes BackgroundNoise 1 times, and has no SpeakerMixer"]


def test_the_background_noise_plays_from_ready_at_the_lab_s_level_reading_no_task_file():
    text = (ROOT / "src" / "OpenRigs.Audio" / "BackgroundNoise.bonsai").read_text(encoding="utf-8-sig")
    assert rules.background_noise_include_problems(lint.ET.fromstring(text.encode("utf-8"))) == []
    early = text.replace("<Name>Ready</Name>", "<Name>Speakers</Name>")
    assert rules.background_noise_include_problems(lint.ET.fromstring(early.encode("utf-8"))) == ["the noise sent does not wait for Ready"]
    louder = text.replace("<aud:LevelDbfs>-60</aud:LevelDbfs>", "<aud:LevelDbfs>-50</aud:LevelDbfs>")
    assert rules.background_noise_include_problems(lint.ET.fromstring(louder.encode("utf-8"))) == ["the noise's level is not the lab's -60 dBFS"]
    read = text.replace("<Name>Ready</Name>", "<Name>TaskConfig</Name>", 1)
    assert "the noise reads the task file, which has no background setting" in rules.background_noise_include_problems(lint.ET.fromstring(read.encode("utf-8")))
    a_file = text.replace('<Combinator xsi:type="aud:ReadBackgroundNoise" />', '<Combinator xsi:type="aud:CheckWaveRate" />')
    assert rules.background_noise_include_problems(lint.ET.fromstring(a_file.encode("utf-8"))) == [
        "the noise is not rig.yml's backgroundNoise (ReadBackgroundNoise)"]


RECORDS = include(lint.EXPERIMENT_RECORDS, "<ConfigRoot>C:\\Root</ConfigRoot><IngestScript>task</IngestScript><ExpectedFiles>auto;harp;Settings/Settings_Rig_;Settings/Settings_Experiment_;WorkflowData/WorkflowData_Events_</ExpectedFiles>")
LIVE = '<Expression xsi:type="lv:StreamDevices" xmlns:lv="clr-namespace:OpenRigs.LiveView;assembly=OpenRigs.LiveView" />'


def test_the_experiment_s_records_are_one_include_last_but_the_live_view(tmp_path):
    logs = include("OpenRigs.Logging:ExperimentLogs.bonsai") + include(lint.BOARD)
    wf = workflow(tmp_path, logs + RECORDS + LIVE)
    assert lint.experiment_records_problems(wf) == [] and lint.experiment_log_problems(wf) == [] and lint.expected_files_problems(wf) == []
    late = workflow(tmp_path / "b", logs + RECORDS + include(lint.REWARDS) + VALVES + LIVE)
    assert lint.experiment_records_problems(late) == ["ExperimentRecords is not the last top-level node (StreamDevices alone may follow)"]
    assert lint.reward_problems(late) == ["Rewards comes after ExperimentRecords"]
    # Rewards are not records: a workflow that gives them includes Rewards itself, before it sends one and before ExperimentRecords.
    sends = '<Expression xsi:type="MulticastSubject"><Name>RewardRequests</Name></Expression>'
    rewards = workflow(tmp_path / "e", logs + include(lint.REWARDS) + VALVES + sends + RECORDS + LIVE)
    assert lint.experiment_records_problems(rewards) == [] and lint.reward_problems(rewards) == []
    assert lint.reward_problems(workflow(tmp_path / "g", logs + sends + RECORDS + LIVE)) == ["the workflow includes Rewards 0 times, not once"]
    # TrialOutcomeEffects sends each rewarded outcome's reward: a sender, held to the same rules.
    effects = include(lint.TRIAL_OUTCOME_EFFECTS)
    assert lint.reward_problems(workflow(tmp_path / "te", logs + include(lint.REWARDS) + VALVES + effects + RECORDS + LIVE)) == []
    assert lint.reward_problems(workflow(tmp_path / "tn", logs + effects + RECORDS + LIVE)) == ["the workflow includes Rewards 0 times, not once"]
    assert lint.reward_problems(workflow(tmp_path / "tl", logs + effects + include(lint.REWARDS) + VALVES + RECORDS + LIVE)) == [
        "Rewards comes after a reward is sent into RewardRequests"]
    assert lint.reward_problems(workflow(tmp_path / "h", logs + sends + include(lint.REWARDS) + VALVES + RECORDS + LIVE)) == [
        "Rewards comes after a reward is sent into RewardRequests"]
    # Every valve after Rewards, which declares what they send into; Rewards with no valve gives no reward.
    assert lint.reward_problems(workflow(tmp_path / "j", logs + VALVES + include(lint.REWARDS) + RECORDS + LIVE)) == [
        "Rewards comes after a RewardValve piece"]
    assert lint.reward_problems(workflow(tmp_path / "k", logs + VALVES + RECORDS + LIVE)) == ["the workflow includes Rewards 0 times, not once"]
    assert lint.reward_problems(workflow(tmp_path / "l", logs + include(lint.REWARDS) + sends + RECORDS + LIVE)) == [
        "the workflow includes Rewards and no RewardValve piece, so no reward can be given"]
    # A task's own copies of what Rewards and the valves hold: the requests declared or read, a delivery or valve sent.
    own = ('<Expression xsi:type="rx:BehaviorSubject"><rx:Name>RewardRequests</rx:Name></Expression>'
           '<Expression xsi:type="SubscribeSubject"><Name>RewardRequests</Name></Expression>'
           '<Expression xsi:type="MulticastSubject"><Name>RewardValves</Name></Expression>'
           '<Expression xsi:type="MulticastSubject"><Name>RewardDelivered</Name></Expression>')
    assert lint.reward_problems(workflow(tmp_path / "i", logs + include(lint.REWARDS) + VALVES + own + sends + RECORDS + LIVE)) == [
        "the workflow declares RewardRequests itself (Rewards does)",
        "the workflow reads RewardRequests itself (each RewardValve piece delivers its rewards)",
        "the workflow sends into RewardValves itself (Rewards and the RewardValve pieces do)",
        "the workflow sends into RewardDelivered itself (Rewards and the RewardValve pieces do)"]
    # A rig without reward valves (no sender) includes none.
    assert lint.reward_problems(wf) == []
    own = workflow(tmp_path / "c", logs + include("OpenRigs.Logging:LogControlEvents.bonsai") + manifest_node("auto")
                   + include(lint.EXPERIMENT_RECORDS, "<ExpectedFiles>auto</ExpectedFiles>"))
    assert lint.experiment_records_problems(own) == [
        "the workflow has its own ExperimentManifest (ExperimentRecords does)", "the workflow has its own LogControlEvents (ExperimentRecords does)",
        "ExperimentRecords has no ConfigRoot", "ExperimentRecords has no IngestScript"]
    assert lint.experiment_log_problems(own) == ["the workflow does not include LogControlEvents once"]
    # Its manifest's ExpectedFiles is the include's, blank by default, and it records the Harp devices.
    unnamed = workflow(tmp_path / "d", logs + include(lint.EXPERIMENT_RECORDS, "<ExpectedFiles>auto</ExpectedFiles>"))
    assert "the workflow has RecordHarpDevices but ExpectedFiles has no harp" in lint.expected_files_problems(unnamed)


def test_the_launch_s_settings_are_one_include_after_experiment_control(tmp_path):
    launch = include(lint.LAUNCH_SETTINGS, "<TaskPath>..\\config\\task.yml</TaskPath>")
    assert lint.launch_settings_problems(workflow(tmp_path, include(lint.EXPERIMENT_CONTROL) + launch)) == []
    own = '<Expression xsi:type="rx:AsyncSubject"><Name>SubjectId</Name></Expression>'
    assert lint.launch_settings_problems(workflow(tmp_path / "b", own + include(lint.LAUNCH_SETTINGS) + include(lint.EXPERIMENT_CONTROL))) == [
        "the workflow declares SubjectId itself (LaunchSettings does)", "LaunchSettings comes before ExperimentControl",
        "LaunchSettings has no TaskPath (the task file run from the editor)"]


def test_a_workflow_makes_the_experiment_folder_or_uses_no_experiment_folder_never_its_own_stand_ins(tmp_path):
    log = include(lint.LOG_LAB_EXPERIMENT)
    none = include(lint.NO_EXPERIMENT_FOLDER)
    assert lint.no_experiment_folder_problems(workflow(tmp_path, log + include(lint.LAUNCH_SETTINGS))) == []
    assert lint.no_experiment_folder_problems(workflow(tmp_path / "b", none)) == []
    assert lint.no_experiment_folder_problems(workflow(tmp_path / "c", log + none)) == [
        "the workflow includes ExperimentLogs 1 and NoExperimentFolder 1 times: one of the two once"]
    assert lint.no_experiment_folder_problems(workflow(tmp_path / "d", "")) == [
        "the workflow includes ExperimentLogs 0 and NoExperimentFolder 0 times: one of the two once"]
    # The QC workflows' old stand-ins: a never-published PathPrefix, and a SubjectId in a file of their own.
    path_prefix = '<Expression xsi:type="rx:BehaviorSubject" TypeArguments="sys:String"><rx:Name>PathPrefix</rx:Name></Expression>'
    own_file(tmp_path / "e", "Extensions/Spouts.bonsai", '<Expression xsi:type="rx:BehaviorSubject"><Name>SubjectId</Name></Expression>')
    spouts = workflow(tmp_path / "e", path_prefix + none + include(lint.LAUNCH_SETTINGS) + include("Extensions\\Spouts.bonsai"))
    assert lint.no_experiment_folder_problems(spouts) == [
        "the workflow declares PathPrefix itself", "the workflow declares SubjectId itself",
        "the workflow includes LaunchSettings and NoExperimentFolder (both declare SubjectId)"]
    published = '<Expression xsi:type="MulticastSubject"><Name>PathPrefix</Name></Expression>'
    assert lint.no_experiment_folder_problems(workflow(tmp_path / "f", log + published)) == ["the workflow publishes PathPrefix itself"]
    # Three QC workflows named their no-mouse subject three ways; NoExperimentFolder has the one.
    own = include(lint.NO_EXPERIMENT_FOLDER, "<SubjectId>_valve-calibration_</SubjectId>")
    assert lint.no_experiment_folder_problems(workflow(tmp_path / "g", own)) == [
        "NoExperimentFolder's SubjectId is _valve-calibration_, not its own _no-experiment_: delete the workflow's value"]
    saved = include(lint.NO_EXPERIMENT_FOLDER, "<SubjectId>_no-experiment_</SubjectId>")
    assert lint.no_experiment_folder_problems(workflow(tmp_path / "h", saved)) == []


def test_no_experiment_folder_declares_what_log_lab_experiment_and_launch_settings_give_the_pieces():
    text = (ROOT / "src" / "OpenRigs.Logging" / "NoExperimentFolder.bonsai").read_text(encoding="utf-8-sig")
    declared = re.findall(r'xsi:type="(rx:\w+Subject)"(?: TypeArguments="([\w:]+)")?>\s*<(?:rx:)?Name>(\w+)<', text)
    assert declared == [("rx:AsyncSubject", "", "SubjectId"), ("rx:BehaviorSubject", "sys:String", "PathPrefix")]
    assert "MulticastSubject" not in text
    assert f"<Value>{lint.NO_EXPERIMENT_SUBJECT}</Value>" in text


def test_a_board_not_named_main_is_filled_from_the_template_s_first_board(tmp_path):
    wf = workflow(tmp_path, include(lint.READ_LAB_RIG) + piece(lint.READY_GATE, PieceStages="HarpClockSet")
                  + named(lint.BOARD, "BehaviorBoard-Arena") + piece(lint.CLOCK_FROM_BOARD, Board="BehaviorBoard-Arena",
                  BoardCommandsSubjectName="BehaviorBoard-Arena.Commands", BoardDataSubjectName="BehaviorBoard-Arena.Data"))
    assert lint.hardware_problems(wf) == []
    text, records, files = lint.filled_rig(wf, tmp_path, "RIG")
    rig = YAML(typ="safe").load(text)
    LabRig.model_validate(rig)
    assert list(rig["behaviorBoard"]) == ["arena"] and records == ["BehaviorBoard-Arena COM1 SERIAL0 1216"]
    assert files == ["BehaviorBoard-Arena/", "BehaviorBoard-Arena/BehaviorBoard-Arena_device.json", "BehaviorBoard-Arena/BehaviorBoard-Arena.yml"]


def test_each_clock_publishes_timestamp_from_its_fast_register():
    # Every pulse width, wait and event time reads Timestamp: a 1 Hz heartbeat clock rounds them to whole seconds.
    def register_into_timestamp(resource: str) -> str:
        root = lint.ET.fromstring((ROOT / "src" / "OpenRigs.Devices" / resource).read_bytes())
        nodes = list(root.find(f"{lint.W}Workflow/{lint.W}Nodes") or [])
        edges = {int(e.get("To", -1)): int(e.get("From", -1)) for e in root.iter(f"{lint.W}Edge")}
        at = next(i for i, n in enumerate(nodes) if n.get(lint.XSI) == "rx:BehaviorSubject" and n.findtext(f"{lint.W}Name") == "Timestamp")
        while not (nodes[at].get(lint.XSI) or "").endswith(":Parse"):
            at = edges[at]
        return next(iter(nodes[at])).get(lint.XSI) or ""
    assert register_into_timestamp("ClockFromBoard.bonsai") == "beh:TimestampedAnalogData"
    assert register_into_timestamp("ClockFromTimestampGenerator.bonsai") == "tsg:TimestampedTimer"


def test_camera_live_controls_declares_each_camera_s_own_parameters(tmp_path):
    # Exposure and gain per camera: <Camera>.ExposureUs and <Camera>.GainDb, the piece the camera.
    face = piece(lint.CAMERA_LIVE_CONTROLS, Camera="Camera-Face", Board="BehaviorBoard-Main")
    body = piece(lint.CAMERA_LIVE_CONTROLS, Camera="Camera-Body", Board="BehaviorBoard-Main")
    two = workflow(tmp_path / "two", include(lint.READ_LAB_RIG) + face + body + include(lint.EXPERIMENT_CONTROL))
    assert {(who, name) for who, kind, name in lint.control_declarations(two) if kind == "parameter"} == {
        ("Camera-Face", "Camera-Face.ExposureUs"), ("Camera-Face", "Camera-Face.GainDb"),
        ("Camera-Body", "Camera-Body.ExposureUs"), ("Camera-Body", "Camera-Body.GainDb")}
    assert lint.declared_twice(two) == []
    copy = "<Parameters><Parameter><Name>Camera-Face.GainDb</Name></Parameter></Parameters>"
    task = workflow(tmp_path / "task", include(lint.READ_LAB_RIG) + face + include(lint.EXPERIMENT_CONTROL, copy))
    assert lint.declared_twice(task) == ["the parameter Camera-Face.GainDb is declared by ExperimentControl and by Camera-Face"]
    live = piece(lint.CAMERA_LIVE_CONTROLS, Camera="Camera-Overhead", Board="BehaviorBoard-Main")
    assert lint.hardware_problems(workflow(tmp_path / "one", MIXED + live)) == []
    assert lint.hardware_problems(workflow(tmp_path / "twice", MIXED + live + live)) == [
        "two CameraLiveControls name Camera Camera-Overhead"]


def test_camera_live_controls_names_every_control_from_its_camera():
    # One externalized Camera reaches every device-named control, so no node keeps an empty device.
    root = lint.ET.fromstring((ROOT / "src" / "OpenRigs.Devices" / "CameraLiveControls.bonsai").read_bytes())
    nodes, edges = lint.nodes_and_edges(lint.child(root, lint.W + "Workflow"))
    camera = next(i for i, n in enumerate(nodes) if n.get(lint.XSI) == "ExternalizedMapping"
                  and any(p.get("DisplayName") == "Camera" for p in n.iter(lint.W + "Property")))
    device_named = {i for i, n in enumerate(nodes) if lint.kind(n).split(":")[-1] in
                    ("DeclareDeviceControls", "OnDeviceParameterSet", "DeviceParameterDefault", "DeviceParameterMaximum", "CameraLiveSetting")}
    assert len(device_named) == 8
    assert device_named <= {b for a, b, _ in edges if a == camera}

def test_camera_live_controls_takes_a_pupil_camera_s_gain_of_20_db():
    # Pupil cameras run at gain 20 in rig.yml: the live control must neither clamp nor refuse it.
    root = lint.ET.fromstring((ROOT / "src" / "OpenRigs.Devices" / "CameraLiveControls.bonsai").read_bytes())
    gain = next(p for p in root.iter(lint.CONTROL + "Parameter") if p.findtext(lint.CONTROL + "Name") == "GainDb")
    assert float(gain.findtext(lint.CONTROL + "Minimum") or "nan") <= 0 and float(gain.findtext(lint.CONTROL + "Maximum") or "nan") >= 20

def test_each_lick_detector_and_wheel_publishes_its_own_subject(tmp_path):
    # Two detectors both on LickStateRight, or two wheels on one subject, passed the lint.
    swapped = MIXED.replace("<LickStateSubjectName>LickStateLeft<", "<LickStateSubjectName>LickStateRight<")
    assert lint.hardware_problems(workflow(tmp_path / "a", swapped)) == [
        "LicketySplit LicketySplit-Left's LickStateSubjectName is LickStateRight, not LickStateLeft"]
    wheel = board_line(lint.RUNNING_WHEEL)
    assert lint.hardware_problems(workflow(tmp_path / "b", MIXED + wheel)) == ["RunningWheel's WheelDistanceSubjectName is not set"]
    same = wheel.replace("</Expression>", "<WheelDistanceSubjectName>WheelDistanceCm</WheelDistanceSubjectName></Expression>")
    assert lint.hardware_problems(workflow(tmp_path / "c", MIXED + same)) == ["two RunningWheels publish WheelDistanceSubjectName WheelDistanceCm"]


def test_the_raw_projector_display_is_a_piece_of_its_own(tmp_path):
    # RawProjectorDisplay opens the projector's window as ProjectorDisplay does.
    raw = include(lint.RAW_PROJECTOR_DISPLAY)
    assert lint.hardware_problems(workflow(tmp_path / "a", MIXED + raw)) == []
    assert lint.hardware_problems(workflow(tmp_path / "b", MIXED + raw + raw)) == ["the workflow includes RawProjectorDisplay more than once"]
    both = lint.hardware_problems(workflow(tmp_path / "c", MIXED + raw + include(lint.PROJECTOR_DISPLAY)))
    assert both == ["the workflow includes ProjectorDisplay and RawProjectorDisplay: one or the other"]
    early = workflow(tmp_path / "d", include(lint.READ_LAB_RIG) + raw + MIXED.replace(include(lint.READ_LAB_RIG), "", 1))
    assert lint.hardware_problems(early) == ["RawProjectorDisplay comes before ReadyGate"]


@pytest.mark.parametrize("log", ["LogVideo", "LogRawVideo"])
def test_upstream_s_video_logs_are_refused(tmp_path, log):
    # The files stay for upstream merges; a lab camera logs itself.
    wf = workflow(tmp_path, MIXED + include(f"OpenRigs.Logging:{log}.bonsai"))
    assert lint.hardware_problems(wf) == [f"the workflow includes {log}, which a lab workflow does not use: each camera piece logs itself (LogVideoFfmpeg)"]

def test_rewards_checks_each_request_against_the_valves_and_logs_each_delivery():
    text = (ROOT / "src" / "OpenRigs.Devices" / "Rewards.bonsai").read_text(encoding="utf-8-sig")
    root = lint.ET.fromstring(text.encode("utf-8"))
    nodes, edges = lint.nodes_and_edges(lint.child(root, lint.W + "Workflow"))
    check = next(i for i, n in enumerate(nodes) if lint.kind(n) == "dev:RequireRewardValve")
    inputs = {label: lint._name(nodes[a]) for a, b, label in edges if b == check}  # pyright: ignore[reportPrivateUsage]
    assert inputs == {"Source1": "RewardRequests", "Source2": "RewardValves"}
    assert lint.shared_mapping_problems(root) == []


def test_a_reward_valve_is_a_piece_named_by_its_valve_on_its_board(tmp_path):
    wf = workflow(tmp_path, MIXED + include(lint.REWARDS) + VALVES)
    assert lint.hardware_problems(wf) == [] and lint.reward_problems(wf) == []
    wrong = (include(lint.REWARD_VALVE, "<Name>Valve-Left</Name><Board>BehaviorBoard-Main</Board><BoardDataSubjectName>BehaviorBoard-Main.Data</BoardDataSubjectName>"
                     "<BoardTriggerCalibratedPulseSubjectName>BehaviorBoard-Main.TriggerCalibratedPulse</BoardTriggerCalibratedPulseSubjectName>")
             + include(lint.REWARD_VALVE, "<Name>RewardValve-Right</Name><Board>BehaviorBoard-Second</Board><BoardDataSubjectName>BehaviorBoard-Second.Data</BoardDataSubjectName>"
                       "<BoardTriggerCalibratedPulseSubjectName>BehaviorBoard-Main.TriggerCalibratedPulse</BoardTriggerCalibratedPulseSubjectName>"))
    assert lint.hardware_problems(workflow(tmp_path / "b", MIXED + include(lint.REWARDS) + wrong)) == [
        "RewardValve Valve-Left is not named RewardValve-<Instance>",
        "RewardValve's Board BehaviorBoard-Second is not a BehaviorBoard piece's Name",
        "RewardValve BehaviorBoard-Second's BoardTriggerCalibratedPulseSubjectName is BehaviorBoard-Main.TriggerCalibratedPulse, "
        "not BehaviorBoard-Second.TriggerCalibratedPulse"]
    twice = workflow(tmp_path / "c", MIXED + include(lint.REWARDS) + VALVES + VALVES)
    assert "two pieces are named RewardValve-Left" in lint.hardware_problems(twice)


def test_every_workflow_with_experiment_control_takes_the_launch_id_from_its_top_level(tmp_path):
    mapping = '<Expression xsi:type="ExternalizedMapping"><Property Name="LaunchId" /></Expression>'
    edge = '</Nodes><Edges><Edge From="0" To="1" Label="Source1" /></Edges></Workflow>'
    group = (f'{mapping}<Expression xsi:type="GroupWorkflow"><Name>Initialization</Name><Workflow><Nodes>{mapping}'
             f'{include(lint.EXPERIMENT_CONTROL)}{edge}</Expression>')
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "main.bonsai").write_text(HEAD + group + edge + "</WorkflowBuilder>", encoding="utf-8")
    assert lint.launch_id_problems(lint.Workflow(tmp_path)) == []
    assert lint.launch_id_problems(workflow(tmp_path / "b", include(lint.EXPERIMENT_CONTROL))) == [
        "ExperimentControl is not given LaunchId by an ExternalizedMapping (map the top-level LaunchId down to ExperimentControl)"]
    inner = group.replace(mapping + include(lint.EXPERIMENT_CONTROL), "<Expression xsi:type=\"WorkflowInput\" />" + include(lint.EXPERIMENT_CONTROL), 1)
    (tmp_path / "c" / "src").mkdir(parents=True)
    (tmp_path / "c" / "src" / "main.bonsai").write_text(HEAD + inner + edge + "</WorkflowBuilder>", encoding="utf-8")
    assert lint.launch_id_problems(lint.Workflow(tmp_path / "c")) == [
        "ExperimentControl is not given LaunchId by an ExternalizedMapping (map the top-level LaunchId down to ExperimentControl)"]
    assert lint.launch_id_problems(workflow(tmp_path / "d", include(lint.NO_EXPERIMENT_FOLDER))) == []
    # The include exposes it, mapped onto ControlServer's LaunchId, which status reports and the manifest records.
    text = (ROOT / "src" / "OpenRigs.Control" / "ExperimentControl.bonsai").read_text(encoding="utf-8-sig")
    assert '<Property Name="LaunchId" DisplayName="LaunchId"' in text and "<p1:LaunchId />" in text


def test_a_device_opened_by_hand_is_refused(tmp_path):
    ns = ('xmlns:beh="clr-namespace:Harp.Behavior;assembly=Harp.Behavior" xmlns:vid="clr-namespace:OpenRigs.Video;assembly=OpenRigs.Video" '
          'xmlns:gl="clr-namespace:Bonsai.Shaders;assembly=Bonsai.Shaders" ')
    nodes = ''.join(f'<Expression xsi:type="Combinator" {ns}><Combinator xsi:type="{t}" /></Expression>'
                    for t in ("beh:Device", "vid:TriggeredSpinnaker", "vid:UsbCamera", "gl:CreateWindow"))
    wf = workflow(tmp_path, nodes)
    window = "Bonsai.Shaders.CreateWindow (ProjectorDisplay or RawProjectorDisplay)"
    assert lint.hand_opened_devices(wf) == ["Harp.Behavior.Device (the board's piece)", "OpenRigs.Video.TriggeredSpinnaker (SpinnakerCamera)",
                                            "OpenRigs.Video.UsbCamera (UsbCamera)", window]
    # A file of the workflow's own counts as the workflow; a lab include's pieces do not.
    (tmp_path / "b" / "src" / "Extensions").mkdir(parents=True)
    (tmp_path / "b" / "src" / "Extensions" / "Camera.bonsai").write_text(HEAD + nodes.split("</Expression>")[2] + "</Expression></Nodes><Edges /></Workflow></WorkflowBuilder>", encoding="utf-8")
    own = workflow(tmp_path / "b", include("Extensions\\Camera.bonsai") + include(lint.BOARD))
    assert lint.hand_opened_devices(own) == ["OpenRigs.Video.UsbCamera (UsbCamera)"]
    # The balance camera is a piece too; including it opens nothing by hand.
    assert lint.hand_opened_devices(workflow(tmp_path / "d", include(lint.DEVICES + "UsbCamera.bonsai"))) == []
    # A serial port opened by hand passed.
    port = 'xmlns:port="clr-namespace:Bonsai.IO.Ports;assembly=Bonsai.System" '
    serial = workflow(tmp_path / "c", ''.join(f'<Expression xsi:type="Combinator" {port}><Combinator xsi:type="port:{t}" /></Expression>'
                                              for t in ("CreateSerialPort", "SerialReadLine", "SerialWriteLine")))
    assert lint.hand_opened_devices(serial) == [f"Bonsai.IO.Ports.{t} (SerialBalance, or the device's piece)" for t in ("CreateSerialPort", "SerialReadLine")]


def test_the_live_view_is_the_last_top_level_node_fed_by_nothing(tmp_path):
    assert lint.live_view_problems(workflow(tmp_path, RECORDS + LIVE)) == []
    assert lint.live_view_problems(workflow(tmp_path / "b", LIVE + RECORDS)) == ["StreamDevices is not the last top-level node"]
    assert lint.live_view_problems(workflow(tmp_path / "c", RECORDS)) == ["the workflow does not have one StreamDevices, at its top level"]
    (tmp_path / "d" / "src").mkdir(parents=True)
    (tmp_path / "d" / "src" / "main.bonsai").write_text(HEAD + RECORDS + LIVE + '</Nodes><Edges><Edge From="0" To="1" Label="Source1" /></Edges></Workflow></WorkflowBuilder>', encoding="utf-8")
    assert lint.live_view_problems(lint.Workflow(tmp_path / "d")) == ["StreamDevices has an edge into or out of it"]


def test_check_workflow_asks_whether_a_workflow_records_nothing_or_logs_trials_of_its_own_includes_too(tmp_path):
    # check-workflow read NoExperimentFolder and LogTrials from the entry file alone, so a workflow that
    # included either from a file of its own was checked as the other kind.
    assert lint.includes(workflow(tmp_path / "a", "")) == {"records_nothing": False, "logs_trials": False}
    wf = own_include(tmp_path / "b", "", include(lint.NO_EXPERIMENT_FOLDER) + include("OpenRigs.Logging:LogTrials.bonsai"))
    assert lint.includes(wf) == {"records_nothing": True, "logs_trials": True}
    script = (ROOT / "src" / "open_rigs" / "scripts" / "check-workflow.ps1").read_text(encoding="utf-8")
    assert "NoExperimentFolder.bonsai" not in script and "LogTrials.bonsai" not in script


def test_a_rig_list_s_description_says_what_rig_it_is_not_its_own_name():
    # DomeHardware's read "The hardware of a DomeHardware rig", worded round the lab-free test.
    for path, signature in lint.RIG_LISTS.items():
        stem = path.split(":")[1].removesuffix(".bonsai")
        text = (ROOT / "src" / "OpenRigs.Devices" / f"{stem}.bonsai").read_text(encoding="utf-8-sig")
        assert stem not in signature and f"<Description>{signature}" in text


@pytest.mark.parametrize("rig_list", lint.RIG_LISTS)
def test_each_rig_list_s_taps_tap_its_devices(tmp_path, rig_list):
    # A preflight names no device; its rig list's taps include taps each one the rig list drives, in order.
    wf = workflow(tmp_path, include(rig_list))
    expected = [name for name, _ in lint.required_devices(wf)]
    if "OpenRigs.Vision:ProjectorDisplay.bonsai" in (ROOT / "src" / "OpenRigs.Devices" / rig_list.split(":")[1]).read_text(encoding="utf-8-sig"):
        expected.append("Projector")
    text = (ROOT / "src" / "OpenRigs.Devices" / lint.RIG_LIST_TAPS[rig_list].split(":")[1]).read_text(encoding="utf-8-sig")
    taps = re.findall(r"<Name>([\w.-]+)</Name>\s*</Expression>\s*<Expression xsi:type=\"Combinator\">\s*<Combinator xsi:type=\"dev:PreflightTap\">\s*<dev:Device>([\w-]+)</dev:Device>", text)
    assert [device for _, device in taps] == expected
    assert [source for source, _ in taps] == [f"{d}.Data" if d != "Projector" else "DrawStimuli" for d in expected]
    assert text.count("<Name>DeviceTicks</Name>") == len(expected)


def test_a_preflight_taps_through_its_rig_list_s_taps(tmp_path):
    checks = include(lint.PREFLIGHT_CHECKS)
    box, taps = (include(lint.DEVICES + name) for name in ("BBoxHardware.bonsai", "BBoxHardwareTaps.bonsai"))
    own = '<Expression xsi:type="Combinator"><Combinator xsi:type="dev:PreflightTap" /></Expression>'
    assert lint.preflight_tap_problems(workflow(tmp_path, checks + box + taps)) == []
    assert lint.preflight_tap_problems(workflow(tmp_path / "b", box)) == [], "not a preflight"
    assert lint.preflight_tap_problems(workflow(tmp_path / "c", checks + box + own)) == [
        "the preflight includes BBoxHardware 1 and BBoxHardwareTaps 0 times: the taps once with their rig list",
        "the preflight taps a device itself (PreflightTap): its rig list's taps include taps every device"]


def _csproj(path: Path, *pins: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    refs = "".join(f'<PackageReference Include="{p.split()[0]}" Version="{p.split()[1]}" />' for p in pins)
    path.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{refs}</ItemGroup></Project>', encoding="utf-8")


def test_a_workflow_project_pinning_a_central_package_at_another_version_fails_naming_it(tmp_path):
    wf = workflow(tmp_path, "")
    _csproj(tmp_path / "src" / "Extensions.csproj", "YamlDotNet 16.3.0", "OpenCV.Net 3.4.2", "Some.Package 1.0.0")
    _csproj(tmp_path / "tests" / "T" / "T.csproj", "yamldotnet 17.1.0")
    _csproj(tmp_path / "src" / "bin" / "Old.csproj", "YamlDotNet 1.0.0")
    versions = lint.central_versions(tmp_path)
    assert versions["yamldotnet"] and versions["opencv.net"] == "3.4.2", "read from src\\Directory.Packages.props"
    problems = lint.central_version_problems(wf)
    assert len(problems) == 1 and problems[0].startswith(f"src\\Extensions.csproj pins YamlDotNet 16.3.0, and the library pins {versions['yamldotnet']} ")
    assert "drop the reference" in problems[0] and "workflow update" in problems[0]
    _csproj(tmp_path / "src" / "Extensions.csproj", "OpenCV.Net 3.4.2")
    assert lint.central_version_problems(wf) == []


def test_an_installed_lint_takes_the_central_versions_from_the_pinned_packages_dependencies(tmp_path):
    wf = workflow(tmp_path, "")
    (tmp_path / ".bonsai").mkdir()
    (tmp_path / ".bonsai" / "Bonsai.config").write_text('<Package id="A.Pkg" version="1.2.0" />\n<Package id="B.Pkg" version="9.0.0" />', encoding="utf-8")
    (tmp_path / "local_packages").mkdir()
    with zipfile.ZipFile(tmp_path / "local_packages" / "A.Pkg.1.2.0.nupkg", "w") as z:
        z.writestr("A.Pkg.nuspec", '<package><metadata><dependencies><group targetFramework=".NETFramework4.7.2">'
                   '<dependency id="YamlDotNet" version="17.1.0" exclude="Build,Analyzers" />'
                   '<dependency id="Bonsai.Core" version="[2.9.0, )" /></group></dependencies></metadata></package>')
    with zipfile.ZipFile(tmp_path / "local_packages" / "A.Pkg.1.1.0.nupkg", "w") as z:
        z.writestr("A.Pkg.nuspec", '<dependency id="YamlDotNet" version="16.3.0" />')
    none = tmp_path / "no.props"
    assert lint.central_versions(tmp_path, none) == {"yamldotnet": "17.1.0", "bonsai.core": "2.9.0", "a.pkg": "1.2.0"}
    _csproj(tmp_path / "src" / "Extensions.csproj", "YamlDotNet 16.3.0", "Bonsai.Core 2.9.0")
    assert [p.split(",")[0] for p in lint.central_version_problems(wf, none)] == ["src\\Extensions.csproj pins YamlDotNet 16.3.0"]


def test_an_installed_lint_takes_the_highest_version_any_pinned_package_needs(tmp_path):
    # 22 Nov 2026: Core went to .41 with Logging; Devices, not rebuilt, still listed Core .40, and the lint wanted .40.
    (tmp_path / ".bonsai").mkdir()
    (tmp_path / ".bonsai" / "Bonsai.config").write_text('<Package id="L" version="1" />\n<Package id="D" version="1" />', encoding="utf-8")
    (tmp_path / "local_packages").mkdir()
    for package, core in (("L", "0.1.1-x.41"), ("D", "0.1.1-x.40")):
        with zipfile.ZipFile(tmp_path / "local_packages" / f"{package}.1.nupkg", "w") as z:
            z.writestr(f"{package}.nuspec", f'<dependency id="OpenRigs.Core" version="[{core}, )" />')
    assert lint.central_versions(tmp_path, tmp_path / "no.props") == {"openrigs.core": "0.1.1-x.41", "l": "1", "d": "1"}


def test_an_installed_lint_takes_a_pinned_package_at_its_own_version(tmp_path):
    # 22 Nov 2026: Devices went to .59 alone; Trials, built before, still listed Devices .58, and the lint wanted .58 of
    # every workflow whose Extensions.csproj references Devices at the .59 it runs.
    (tmp_path / ".bonsai").mkdir()
    (tmp_path / ".bonsai" / "Bonsai.config").write_text('<Package id="OpenRigs.Devices" version="0.1.1-x.59" />\n<Package id="OpenRigs.Trials" version="0.1.1-x.19" />', encoding="utf-8")
    (tmp_path / "local_packages").mkdir()
    for package, version, spec in (("OpenRigs.Devices", "0.1.1-x.59", ""), ("OpenRigs.Trials", "0.1.1-x.19", '<dependency id="OpenRigs.Devices" version="[0.1.1-x.58, )" />')):
        with zipfile.ZipFile(tmp_path / "local_packages" / f"{package}.{version}.nupkg", "w") as z:
            z.writestr(f"{package}.nuspec", spec)
    assert lint.central_versions(tmp_path, tmp_path / "no.props")["openrigs.devices"] == "0.1.1-x.59"