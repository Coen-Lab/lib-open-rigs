"""The lab's rules for a workflow's src\\main.bonsai, read from its XML (standard library only).

Each task's tests\\test_workflow.py calls these on its own workflow, so a rule found in one task holds in every task:
    from open_rigs.rigs import workflow_lint as lint
    WORKFLOW = lint.Workflow(Path(__file__).parents[1])
    def test_no_trial_graph_closes_a_shared_subject():
        assert lint.subjects_closed_by_a_nested_multicast(WORKFLOW) == []
Each check returns what breaks the rule (empty when it holds), so a failing test names it.
"""

from __future__ import annotations

import copy
import io
import json
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePath, PureWindowsPath

from open_rigs.rigs.lab_rig import PREFLIGHT_DEVICE_KINDS

W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
RX = "{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}"
SCRIPT = "{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}"
AUDIO = "{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}"
CONTROL = "{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}"
CORE = "{clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core}"
FINITE = ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")
WALL_CLOCK = ("rx:Timer", "rx:Delay", "rx:DelaySubscription", "rx:Throttle", "rx:Timeout", "rx:Interval", "rx:TimeInterval",
              "rx:Timestamp", "gl:Timer", "gl:Delay")
PER_ELEMENT = {"rx:SelectMany", "rx:CreateObservable", "rx:Concat", "rx:Switch"}
# The subjects a lab block declares for the workflow, and their kind: a multicast into them is not seen in the workflow's XML.
SPEAKER_MIXER = "OpenRigs.Audio:SpeakerMixer.bonsai"
SPEAKER_MIXER_SOUNDS = ("StimulusSounds", "RewardToneSounds", "PenaltySounds", "BackgroundNoise")
ZAPIT_INCLUDE = "OpenRigs.Devices:ConnectZapit.bonsai"
ZAPIT_FOR_TASK = "OpenRigs.Devices:ConnectForTask.bonsai"
REWARDS = "OpenRigs.Devices:Rewards.bonsai"
MANUAL_PULSES = "OpenRigs.Devices:ManualPulses.bonsai"
BACKGROUND_NOISE = "OpenRigs.Audio:BackgroundNoise.bonsai"
EXPERIMENT_RECORDS = "OpenRigs.Logging:ExperimentRecords.bonsai"
LAUNCH_SETTINGS = "OpenRigs.Control:LaunchSettings.bonsai"
LOG_LAB_EXPERIMENT = "OpenRigs.Logging:ExperimentLogs.bonsai"
NO_EXPERIMENT_FOLDER = "OpenRigs.Logging:NoExperimentFolder.bonsai"
# The subject every NoExperimentFolder workflow's pieces see: no mouse's, one name for them all.
NO_EXPERIMENT_SUBJECT = "_no-experiment_"
EXPERIMENT_CONTROL = "OpenRigs.Control:ExperimentControl.bonsai"
PLAY_PENALTY_SOUND = "OpenRigs.Audio:PlayPenaltySound.bonsai"
SET_ZAPIT_TRIGGER_LINE = "OpenRigs.Devices:SetZapitTriggerLine.bonsai"
LOG_RNG_SEED = "OpenRigs.Logging:LogRngSeed.bonsai"
TASK_LOGS = "OpenRigs.Logging:TaskLogs.bonsai"
TRAINING_STAGE = "OpenRigs.Control:TrainingStage.bonsai"
# The device pieces and the core (see hardware_problems). A piece's own subjects are named by its properties.
DEVICES = "OpenRigs.Devices:"
READ_LAB_RIG = DEVICES + "ReadLabRig.bonsai"
READY_GATE = DEVICES + "ReadyGate.bonsai"
CLOCK_FROM_GENERATOR = DEVICES + "ClockFromTimestampGenerator.bonsai"
CLOCK_FROM_BOARD = DEVICES + "ClockFromBoard.bonsai"
CLOCKS = (CLOCK_FROM_GENERATOR, CLOCK_FROM_BOARD)
BOARD = DEVICES + "BehaviorBoard.bonsai"
LICKETY_SPLIT = DEVICES + "LicketySplit.bonsai"
LICK_SPOUT_STAGE = DEVICES + "LickSpoutStage.bonsai"
CAMERA = DEVICES + "SpinnakerCamera.bonsai"
CAMERA_TRIGGERS = DEVICES + "CameraTriggers.bonsai"
CAMERA_LIVE_CONTROLS = DEVICES + "CameraLiveControls.bonsai"
PROJECTOR_DISPLAY = "OpenRigs.Vision:ProjectorDisplay.bonsai"
RAW_PROJECTOR_DISPLAY = "OpenRigs.Vision:RawProjectorDisplay.bonsai"  # the same window unmapped: one or the other
PULSE_OUTPUTS = DEVICES + "PulseOutputs.bonsai"
RUNNING_WHEEL = DEVICES + "RunningWheel.bonsai"
REWARD_VALVE = DEVICES + "RewardValve.bonsai"
# What each piece takes: Name, Board and Camera name a device; X.Commands / X.Data are the subject properties
# (CommandsSubjectName, BoardDataSubjectName, ...) that must equal that device's name plus the suffix.
PIECES = {
    CLOCK_FROM_GENERATOR: ("Name", "Name.Commands", "Name.Data"),
    CLOCK_FROM_BOARD: ("Board", "Board.Commands", "Board.Data"),
    BOARD: ("Name", "Name.Commands", "Name.Data"),
    PULSE_OUTPUTS: ("Board", "Board.Commands", "Board.Data", "Board.TriggerCalibratedPulse"),
    REWARD_VALVE: ("Name", "Board", "Board.TriggerCalibratedPulse"),
    DEVICES + "ZapitTriggerLine.bonsai": ("Board",),
    RUNNING_WHEEL: ("Board", "Board.Commands", "Board.Data"),
    LICKETY_SPLIT: ("Name", "Name.Commands", "Name.Data"),
    LICK_SPOUT_STAGE: (),
    CAMERA: ("Name", "Name.Data", "Board", "Board.Data"),
    CAMERA_TRIGGERS: ("Board", "Board.Commands", "Board.Data"),
    CAMERA_LIVE_CONTROLS: ("Camera", "Board"),
    MANUAL_PULSES: ("Board", "Board.Commands"),
    SPEAKER_MIXER: (),
    PROJECTOR_DISPLAY: (),
    RAW_PROJECTOR_DISPLAY: (),
}
# The Kind each named piece's Name starts with (Kind-Instance): RigDevice finds the entry by the Instance alone, in the
# piece's own rig.yml group, so a BehaviorBoard piece named Camera-Main would open the board main and log as a camera.
KINDS = {CLOCK_FROM_GENERATOR: "TimestampGeneratorGen3", BOARD: "BehaviorBoard", LICKETY_SPLIT: "LicketySplit", CAMERA: "Camera",
         REWARD_VALVE: "RewardValve"}
ONE_EACH = (LICK_SPOUT_STAGE, SPEAKER_MIXER, PROJECTOR_DISPLAY, RAW_PROJECTOR_DISPLAY)  # pieces a workflow has at most once
# Pieces of which a workflow has one or the other, never both: each opens the projector's window.
EXCLUSIVE = ((PROJECTOR_DISPLAY, RAW_PROJECTOR_DISPLAY),)
# Each piece that declares its controls named from its device (OpenRigs.Control DeclareDeviceControls), by the property
# naming the device: a workflow has at most one per device (CameraLiveControls declares <Camera>.ExposureUs, a
# RewardValve its manual drop <Name>.Reward).
DEVICE_DECLARERS = {CAMERA_LIVE_CONTROLS: "Camera", REWARD_VALVE: "Name"}
# The stage each piece announces into ReadyStages (its AnnounceStage's Stage), which ReadyGate must wait for; a camera
# announces its own Name. test_ready_stages checks each piece's file (or the file it announces from) says the same.
READY_STAGES = {CLOCK_FROM_GENERATOR: "HarpClockSet", CLOCK_FROM_BOARD: "HarpClockSet", LICK_SPOUT_STAGE: "SpoutsHomed",
                PULSE_OUTPUTS: "PulsesEnabled", SPEAKER_MIXER: "MixerStarted"}
# A lick detector's lick state subject is LickState<Instance> (LicketySplit-Left: LickStateLeft), the name TrialLoop's tasks read.
LICK_STATE = "LickState"
# Upstream's video logs, kept for upstream merges: a lab camera logs itself (SpinnakerCamera, LogVideoFfmpeg).
RETIRED_LOGS = {"OpenRigs.Logging:LogVideo.bonsai": "LogVideoFfmpeg", "OpenRigs.Logging:LogRawVideo.bonsai": "LogVideoFfmpeg"}
# ClockFromBoard also announces HarpClockRead at the board's first heartbeat. With SetClock false it never sets the
# board's clock, so Ready waits for that in place of HarpClockSet, and the workflow records nothing (NoExperimentFolder).
CLOCK_READ = "HarpClockRead"
# The rig.yml group of each piece that is a device of its own, and the Harp WhoAmI of those that are Harp boards.
DEVICE_GROUPS = {CLOCK_FROM_GENERATOR: "timestampGeneratorGen3", BOARD: "behaviorBoard", LICKETY_SPLIT: "licketySplit", CAMERA: "camera"}
WHO_AM_I = {"behaviorBoard": 1216, "timestampGeneratorGen3": 1158, "stepperDriver": 1130, "licketySplit": 1400}
LICK_SPOUT_DRIVERS = ("StepperDriver-A", "StepperDriver-B")  # fixed: the stage's C# drives drivers a and b
# Lab includes read from the package (never from the workflow), found in OpenRigs.Devices.dll by the start of their
# Description: a rig list holds only pieces, and is expanded in place.
RIG_LISTS = {DEVICES + "DomeHardware.bonsai": "The hardware of a two-spout rig with a lick spout stage, projector and speakers", DEVICES + "BBoxHardware.bonsai": "The behaviour boxes' hardware"}
# A preflight's check, the same for every rig kind: it declares the Confirm command (DeclareControls).
PREFLIGHT_CHECKS = DEVICES + "PreflightChecks.bonsai"
# Each rig list's taps: its devices' data as a preflight's ticks (PreflightTap into DeviceTicks), so a preflight names none.
RIG_LIST_TAPS = {path: path.removesuffix(".bonsai") + "Taps.bonsai" for path in RIG_LISTS}
SIGNATURES = RIG_LISTS | {LICK_SPOUT_STAGE: "The two-driver lick spout stage", REWARD_VALVE: "One reward valve, Name",
                          REWARDS: "Every reward a task gives",
                          MANUAL_PULSES: "A person's pulses on a Behavior board's lines",
                          CAMERA_LIVE_CONTROLS: "Optional live controls for one SpinnakerCamera piece",
                          PREFLIGHT_CHECKS: "A preflight's check, the same for every rig kind"}
# lib-open-rigs' own source, when this module runs from it (its tests); a workflow's lint reads its package instead.
LIB_SRC = Path(__file__).parents[2]
BLOCK_SUBJECTS = {
    ZAPIT_INCLUDE: {"ZapitCommands": "rx:BehaviorSubject", "ZapitReplies": "rx:PublishSubject"},
    REWARDS: {"RewardRequests": "rx:BehaviorSubject", "RewardValves": "rx:ReplaySubject", "RewardDelivered": "rx:BehaviorSubject"},
    SPEAKER_MIXER: {name: "rx:BehaviorSubject" for name in SPEAKER_MIXER_SOUNDS},
    READY_GATE: {"ReadyStages": "rx:ReplaySubject", "HarpClockSet": "rx:AsyncSubject", "Ready": "rx:AsyncSubject"},
    LICK_SPOUT_STAGE: {"LickSpoutStage.Commands": "rx:BehaviorSubject","LickSpoutStage.Homed": "rx:BehaviorSubject",
                       "StepperDriver-A.Commands": "rx:BehaviorSubject", "StepperDriver-B.Commands": "rx:BehaviorSubject"},
    CLOCK_FROM_GENERATOR: {"Timestamp": "rx:BehaviorSubject"},
    CLOCK_FROM_BOARD: {"Timestamp": "rx:BehaviorSubject"},
}
# Lab includes that include a BLOCK_SUBJECTS block, so a workflow including one has that block's subjects.
CARRIED_BLOCKS = {ZAPIT_FOR_TASK: ZAPIT_INCLUDE}
# A piece's command subject is a BehaviorSubject, its data subject a PublishSubject (named by its properties).
PIECE_SUBJECTS = {"CommandsSubjectName": "rx:BehaviorSubject", "DataSubjectName": "rx:PublishSubject"}
# The lab's Bonsai setup, one copy that each workflow's .bonsai\Setup.ps1 runs from the open-rigs it pins.
SETUP_BONSAI = Path(__file__).parents[1] / "scripts" / "setup-bonsai.ps1"
SETUP_BONSAI_RUN = "open_rigs\\scripts\\setup-bonsai.ps1"
# The prefix each check names a type by ("core:HarpDelay"), by CLR namespace. A workflow's own prefix can differ: a Bonsai
# save numbers those of packages that fix none (OpenRigs.Core as p1:), and a workflow may name it orc:.
PREFIXES = {"Bonsai.Reactive": "rx", "Bonsai.Dsp": "dsp", "Bonsai.Shaders": "gl", "Bonsai.Mixer": "mix", "OpenRigs.Core": "core",
            "OpenRigs.Audio": "aud", "OpenRigs.Control": "orcl", "OpenRigs.Devices.Zapit": "zap"}


class Workflow:
    """A workflow repository's src\\main.bonsai (or another workflow in src), as text and as XML, its root's xsi:type
    prefixes those of PREFIXES. Each workflow file of its own it includes (an IncludeWorkflow Path that is no package's)
    is the workflow's as much as its own nodes: its Workflow is put inside the include node, as Bonsai builds it, so
    every check reads it, and `texts` holds each file's text once per inclusion (`text` all of them). An own include
    Bonsai would not find stops the lint (ValueError), as it stops Bonsai."""

    def __init__(self, repo: Path, file: str = "main.bonsai"):
        self.repo = Path(repo)
        self.file = file
        entry = self.repo / "src" / file
        self.texts = [entry.read_text(encoding="utf-8-sig")]
        self.root = _parse(self.texts[0])
        self._inline(self.root, [entry.resolve()])
        self.text = "\n".join(self.texts)

    def own_include(self, path: str) -> Path:
        """The file Bonsai 2.9 opens for an own include's Path: IncludeWorkflowBuilder adds .bonsai to a Path with no
        extension and opens it as given (File.OpenRead), so a relative Path is relative to the working directory, which
        Bonsai.exe sets to the launched workflow's folder (src) for the editor and --no-editor alike; never relative to
        the including file's folder."""
        path = path if PureWindowsPath(path).suffix else path + ".bonsai"
        if PureWindowsPath(path).anchor:
            return Path(path)
        return (self.repo / "src" / self.file).parent / path.replace("\\", "/")

    def _inline(self, root: ET.Element, including: list[Path]) -> None:
        for node in list(root.iter(W + "Expression")):
            path = node.get("Path") or ""
            if node.get(XSI) != "IncludeWorkflow" or not path or _is_package(path):
                continue
            target = self.own_include(path).resolve()
            where = "src\\" + str(PureWindowsPath(including[-1].relative_to((self.repo / "src").resolve())))
            if not target.is_file():
                raise ValueError(f"{where} includes {path}, which Bonsai opens as {target} (from src, the launched "
                                 "workflow's folder), and there is no such file")
            if target in including:
                raise ValueError(f"{where}'s include {path} includes itself")
            text = target.read_text(encoding="utf-8-sig")
            self.texts.append(text)
            own = _parse(text)
            self._inline(own, [*including, target])
            node.append(child(own, W + "Workflow"))

    def named(self, name: str) -> ET.Element:
        """The first node named `name`, at any depth; a ValueError naming it when there is none (not a bare StopIteration)."""
        return _named(self.root, name, f"src\\{self.file}")


def _is_package(path: str) -> bool:
    """Whether an IncludeWorkflow Path names a package's embedded workflow (Bonsai's IsEmbeddedResourcePath: a colon,
    and not a rooted file path)."""
    return ":" in path and not PureWindowsPath(path).anchor


def _parse(text: str) -> ET.Element:
    """A workflow file's XML, its xsi:type prefixes those of PREFIXES."""
    root = ET.fromstring(text.encode("utf-8"))
    ours = {p: PREFIXES[ns] for p, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]*);', text) if ns in PREFIXES}
    for e in root.iter():
        prefix, _, local = (e.get(XSI) or "").partition(":")
        if local and prefix in ours:
            e.set(XSI, f"{ours[prefix]}:{local}")
    return root


def _named(root: ET.Element, name: str, where: str) -> ET.Element:
    found = next((e for e in root.iter(W + "Expression") if e.findtext(W + "Name") == name), None)
    if found is None:
        raise ValueError(f"{where} has no node named {name}")
    return found


def kind(node: ET.Element) -> str:
    """A node's type: its combinator's for a Combinator node."""
    combinator = node.find(W + "Combinator")
    return (combinator.get(XSI) if combinator is not None else node.get(XSI)) or ""


def child(element: ET.Element, tag: str) -> ET.Element:
    """`element`'s first `tag` child; a workflow without it is malformed, so its absence fails loudly."""
    found = element.find(tag)
    if found is None:
        raise ValueError(f"{element.findtext(W + 'Name') or element.tag} has no {tag.removeprefix(W)}")
    return found


def nodes_and_edges(workflow: ET.Element) -> tuple[list[ET.Element], list[tuple[int, int, str]]]:
    """A Workflow element's nodes and edges (from, to, label; "" for an unlabelled edge)."""
    edges = [(int(e.attrib["From"]), int(e.attrib["To"]), e.get("Label", "")) for e in child(workflow, W + "Edges")]
    return list(child(workflow, W + "Nodes")), edges


def scope(node: ET.Element) -> tuple[list[ET.Element], list[tuple[int, int, str]]]:
    """A nested workflow's nodes and edges (from, to, label)."""
    return nodes_and_edges(child(node, W + "Workflow"))


def upstream(edges: list[tuple[int, int, str]], start: int) -> set[int]:
    """Every node with a path to `start`."""
    seen, frontier = set(), [start]
    while frontier:
        to = frontier.pop()
        for a, b, _ in edges:
            if b == to and a not in seen:
                seen.add(a)
                frontier.append(a)
    return seen


def _scopes(root: ET.Element):
    for workflow in root.iter(W + "Workflow"):
        yield nodes_and_edges(workflow)


def wall_clock_waits(wf: Workflow, group: str = "TaskLogic") -> list[str]:
    """Every wall-clock wait in `group` (plan v11: a task waits on the Harp clock, Core HarpDelay on Timestamp), as
    "<name or type> (<type>)"."""
    found = [f"{e.findtext(W + 'Name') or kind(e)} ({kind(e)})" for e in wf.named(group).iter(W + "Expression") if kind(e) in WALL_CLOCK]
    if "TimeOfDay" in ET.tostring(wf.named(group), encoding="unicode"):
        found.append("TimeOfDay")
    return found


def harp_delays_off_the_clock(wf: Workflow, group: str = "TaskLogic") -> tuple[int, list[str]]:
    """(the HarpDelays in `group`, the names of those whose clock, Source2, is not the Timestamp subject)."""
    count, off = 0, []
    for nodes, edges in _scopes(wf.named(group)):
        for i, node in enumerate(nodes):
            if kind(node) == "core:HarpDelay":
                count += 1
                clocks = [nodes[a] for a, b, label in edges if b == i and label == "Source2"]
                if not (len(clocks) == 1 and clocks[0].findtext(W + "Name") == "Timestamp"):
                    off.append(node.findtext(W + "Name") or "HarpDelay")
    return count, off


def finite_event_senders(wf: Workflow) -> tuple[int, list[str]]:
    """(the multicasts into Events, the finite operators upstream of any): a MulticastSubject passes on its source's
    completion, so a Take ahead of it completed the shared subject at trial 1 (9 Nov 2026). A sender that
    logs once takes its one value after the multicast."""
    senders, finite = 0, []
    for nodes, edges in _scopes(wf.root):
        for i, node in enumerate(nodes):
            if node.get(XSI) == "MulticastSubject" and node.findtext(W + "Name") == "Events":
                senders += 1
                finite += [kind(nodes[a]) for a in upstream(edges, i) if kind(nodes[a]) in FINITE]
    return senders, finite


def subjects_closed_by_a_nested_multicast(wf: Workflow) -> list[str]:
    """Each MulticastSubject inside a per-element nested graph whose subject is not a BehaviorSubject declared in the
    workflow, by a lab block it includes, or inside that graph. Bonsai 2.9 passes a multicast source's completion to a
    PublishSubject or ReplaySubject, so the first trial's graph closes it for good (seen on a rig, 14 Nov 2026)."""
    declared: dict[str, str] = {}
    pieces = hardware(wf)
    carried = {inner for outer, inner in CARRIED_BLOCKS.items() if f'Path="{outer}"' in wf.text}
    for path, subjects in BLOCK_SUBJECTS.items():
        if f'Path="{path}"' in wf.text or path in carried or any(p == path for p, _ in pieces):
            declared.update(subjects)
    for _, props in pieces:
        declared.update({props[p]: subject for p, subject in PIECE_SUBJECTS.items() if props.get(p)})

    def is_subject(n: ET.Element) -> bool:
        return (n.get(XSI) or "").startswith("rx:") and (n.get(XSI) or "").endswith("Subject")

    for n in wf.root.iter(W + "Expression"):
        if is_subject(n):
            declared[_name(n)] = n.get(XSI, "")
    found: list[str] = []

    def walk(workflow: ET.Element, nested: bool, local: set[str]) -> None:
        nodes = child(workflow, W + "Nodes")
        local = local | {_name(n) for n in nodes if is_subject(n)}
        for n in nodes:
            if n.get(XSI) == "MulticastSubject" and nested:
                name = n.findtext(W + "Name", "")
                if name not in local and declared.get(name) != "rx:BehaviorSubject":
                    found.append(name)
            inner = n.find(W + "Workflow")
            if inner is not None:
                per = n.get(XSI) in PER_ELEMENT
                walk(inner, nested or per, set() if per else local)

    walk(child(wf.root, W + "Workflow"), False, set())
    return found


def speaker_mixer_problems(wf: Workflow) -> list[str]:
    """Every sound plays through OpenRigs.Audio's SpeakerMixer, included once, which loads the speakers and starts the
    mixer no sooner than 3 s after Start: the workflow starts no mixer, makes no source and loads no speakers itself."""
    mixers = [p for p, _ in hardware(wf)].count(SPEAKER_MIXER)  # its own, or its rig list's
    problems = [] if mixers == 1 else ["the workflow does not include OpenRigs.Audio's SpeakerMixer once"]
    prefixes = [p for p, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+);', wf.text) if ns == "Bonsai.Mixer"]
    problems += [f"the workflow uses Bonsai.Mixer's {op} itself" for p in prefixes for op in sorted(set(re.findall(r'xsi:type="' + p + r':(\w+)"', wf.text)))]
    if ":LoadSpeakers" in wf.text:
        problems.append("the workflow loads the speakers itself (SpeakerMixer's Speakers)")
    if "MixerBootDelay" in wf.text:
        problems.append("the workflow names its own MixerBootDelay")
    return problems


def sound_level_problems(wf: Workflow) -> list[str]:
    """Every sound is played at a level from the task file (levelDbfs, in dBFS RMS): each SpeakerBuffer and
    PenaltySoundBuffer has LevelDbfs mapped from a TaskParameters ...LevelDbfs selector, and nothing scales a sound
    itself (no ConvertScale, Rescale or Gain of a sound): no task computes levels."""
    problems = []
    for nodes, edges in _scopes(wf.root):
        for i, node in enumerate(nodes):
            if kind(node) not in ("aud:SpeakerBuffer", "aud:PenaltySoundBuffer"):
                continue
            mapped = [nodes[a] for a, b, _ in edges if b == i and nodes[a].get(XSI) in ("InputMapping", "PropertyMapping")]
            names = [p.get("Name") for m in mapped for p in m.iter(W + "Property")]
            selectors = [nodes[a].findtext(W + "Selector") or "" for a in upstream(edges, i)]
            if "LevelDbfs" not in names or not any(s.startswith("TaskParameters.") and s.endswith("LevelDbfs") for s in selectors):
                problems.append(f"{node.findtext(W + 'Name') or kind(node)} is not given LevelDbfs from the task file's levelDbfs")
    kinds = {kind(n) for n in wf.root.iter(W + "Expression")}
    problems += [f"the workflow scales a sound itself ({op})" for op in ("dsp:ConvertScale", "dsp:Rescale") if op in kinds]
    if any(e.tag == AUDIO + "Gain" for e in wf.root.iter()) or 'Property Name="Gain"' in wf.text:
        problems.append("a sound's Gain is set in the workflow")
    return problems


def _name(n: ET.Element) -> str:
    return n.findtext(W + "Name") or n.findtext(RX + "Name") or ""


def package_include(wf: Workflow, path: str) -> ET.Element:
    """A lab include's workflow (`path` as an IncludeWorkflow names it, of those in SIGNATURES): from lib-open-rigs'
    source when this module runs from it, else from the OpenRigs.Devices package Bonsai.config pins, in local_packages."""
    package, resource = path.split(":")
    source = LIB_SRC / package / resource.replace(".", "/", resource.count(".") - 1)
    if source.is_file():
        return ET.fromstring(source.read_text(encoding="utf-8-sig").encode("utf-8"))
    config = (wf.repo / ".bonsai" / "Bonsai.config").read_text(encoding="utf-8-sig")
    version = re.search(rf'<Package id="{re.escape(package)}" version="([^"]+)"', config)
    if version is None:
        raise ValueError(f".bonsai\\Bonsai.config does not pin {package}, which holds {resource}")
    nupkg = wf.repo / "local_packages" / f"{package}.{version.group(1)}.nupkg"
    with zipfile.ZipFile(nupkg) as z:
        dll = z.read(next(n for n in z.namelist() if n.endswith(f"lib/net472/{package}.dll"))).decode("latin-1")
    at = dll.find("<Description>" + SIGNATURES[path])
    if at < 0:
        raise ValueError(f"{nupkg.name} has no {resource} (no Description starting '{SIGNATURES[path]}')")
    start, end = dll.rindex("<WorkflowBuilder", 0, at), dll.index("</WorkflowBuilder>", at) + len("</WorkflowBuilder>")
    return ET.fromstring(dll[start:end].encode("latin-1"))


def _properties(node: ET.Element) -> dict[str, str]:
    return {e.tag.removeprefix(W): e.text or "" for e in node if e.tag.startswith(W) and len(e) == 0}


def hardware(wf: Workflow) -> list[tuple[str, dict[str, str]]]:
    """Every core include and device piece the workflow includes (PIECES, READ_LAB_RIG, READY_GATE), in the
    workflow's order, with the properties the workflow gives it; an included rig list (RIG_LISTS) and each workflow file
    of the workflow's own it includes are expanded in place, their own settings overridden by those the workflow gives
    the include."""
    return [(path, props) for path, props, _ in _hardware(wf)]


def _hardware(wf: Workflow) -> list[tuple[str, dict[str, str], bool]]:
    """hardware(), each piece with whether it runs behind a per-element operator (a SelectMany that starts it only on a
    rig whose rig.yml has its device), so it may never run."""
    known = set(PIECES) | {READ_LAB_RIG, READY_GATE}
    found: list[tuple[str, dict[str, str], bool]] = []

    def walk(workflow: ET.Element, given: dict[str, str], conditional: bool) -> None:
        nodes, edges = nodes_and_edges(workflow)
        for j, node in enumerate(nodes):
            path = node.get("Path") or ""
            props = _properties(node)
            for i, _, _ in (e for e in edges if e[1] == j and nodes[e[0]].get(XSI) == "ExternalizedMapping"):
                for p in nodes[i].iter(W + "Property"):
                    outer = p.get("DisplayName") or p.get("Name")
                    if outer in given:
                        props[p.get("Name") or ""] = given[outer]
            if path in known:
                found.append((path, props, conditional))
            elif path in RIG_LISTS:
                walk(child(package_include(wf, path), W + "Workflow"), props, conditional)
            inner = node.find(W + "Workflow")  # a group's, or an own include's (Workflow puts it there)
            if inner is not None:
                own = node.get(XSI) == "IncludeWorkflow"
                walk(inner, props if own else {}, conditional or node.get(XSI) in PER_ELEMENT)

    walk(child(wf.root, W + "Workflow"), {}, False)
    return found


def _rig_list_problems(wf: Workflow) -> list[str]:
    """Each property a workflow gives a rig list that the rig list does not expose (Bonsai ignores it silently)."""
    problems = []
    for node in wf.root.iter(W + "Expression"):
        path = node.get("Path") or ""
        if path in RIG_LISTS:
            exposed = {p.get("DisplayName") or p.get("Name") or "" for m in package_include(wf, path).iter(W + "Expression")
                       if m.get(XSI) == "ExternalizedMapping" for p in m.iter(W + "Property")}
            name = path.split(":")[1].removesuffix(".bonsai")
            problems += [f"{name} is given {p}, which it does not expose ({', '.join(sorted(exposed)) or 'none'})"
                         for p in _properties(node) if p not in exposed and p not in ("Name", "Description")]
    return problems


def _stages(text: str | None) -> list[str]:
    return [s.strip() for s in (text or "").split(",") if s.strip()]


def required_devices(wf: Workflow) -> list[tuple[str, str]]:
    """Each device the workflow drives, as (its name, its rig.yml group), in the workflow's order: the devices its
    pieces name, and the lick spout stage's two stepper drivers."""
    devices = []
    for path, props in hardware(wf):
        if path in DEVICE_GROUPS:
            devices.append((props.get("Name", ""), DEVICE_GROUPS[path]))
        elif path == LICK_SPOUT_STAGE:
            devices += [(name, "stepperDriver") for name in LICK_SPOUT_DRIVERS]
    return devices


def hardware_problems(wf: Workflow) -> list[str]:
    """The workflow's devices are the lab's pieces, put together so that any combination builds and runs: ReadLabRig
    and ReadyGate once each, ReadLabRig first (a rig list holds none: a piece's log waits for PathPrefix, whose
    ExperimentLogs reads RigConfigs), ReadyGate before every piece (a board's log waits for HarpClockSet, which ReadyGate
    declares); exactly one clock; each piece's Name unique and its subject properties that Name's .Commands and .Data;
    every Board a Behavior board piece and every Camera a camera piece; one CameraTriggers exactly when there is a
    camera, expecting every camera; ReadyGate's PieceStages exactly the stages the pieces announce (HarpClockSet, or
    HarpClockRead for a ClockFromBoard with SetClock false, which only a NoExperimentFolder workflow may have,
    SpoutsHomed for the lick spout stage, each camera's Name, MixerStarted for SpeakerMixer) and its WorkflowStages
    exactly those the workflow announces itself (its own AnnounceStage nodes, such as a calibration's CalibrationReady);
    and the workflow makes no Ready report or StartCameras of its own. A named piece's Name starts with its Kind
    (KINDS); CameraLiveControls' Board is its camera's, and CameraTriggers' every camera's. The workflow's own included
    files count as the workflow; a piece behind a per-element operator (a SelectMany that runs it only on a rig with its
    device) may never announce, so ReadyGate does not wait for it: the workflow waits with a stage of its own."""
    walked = _hardware(wf)
    pieces = [(p, props) for p, props, _ in walked]
    paths = [p for p, _ in pieces]
    problems = [f"the workflow includes {p.split(':')[1].removesuffix('.bonsai')} {paths.count(p)} times, not once"
                for p in (READ_LAB_RIG, READY_GATE) if paths.count(p) != 1]
    clocks = [p for p in paths if p in CLOCKS]
    if len(clocks) != 1:
        problems.append(f"the workflow has {len(clocks)} clocks, not one (ClockFromTimestampGenerator or ClockFromBoard)")
    problems += [f"the workflow includes {p.split(':')[1].removesuffix('.bonsai')} more than once"
                 for p in ONE_EACH if paths.count(p) > 1]
    problems += [f"the workflow includes {' and '.join(p.split(':')[1].removesuffix('.bonsai') for p in group)}: one or the other"
                 for group in EXCLUSIVE if all(p in paths for p in group)]
    for p, prop in DEVICE_DECLARERS.items():
        devices = [props.get(prop, "") for q, props in pieces if q == p]
        problems += [f"two {p.split(':')[1].removesuffix('.bonsai')} name {prop} {d}" for d in sorted({d for d in devices if devices.count(d) > 1})]
    for p, props in pieces:
        name = props.get("Name", "")
        if p == LICKETY_SPLIT and name.startswith(KINDS[p] + "-"):
            want = LICK_STATE + name.removeprefix(KINDS[p] + "-")
            if props.get("LickStateSubjectName") != want:
                problems.append(f"LicketySplit {name}'s LickStateSubjectName is {props.get('LickStateSubjectName') or 'not set'}, not {want}")
    wheels = [props.get("WheelDistanceSubjectName", "") for p, props in pieces if p == RUNNING_WHEEL]
    problems += ["RunningWheel's WheelDistanceSubjectName is not set" for w in wheels if not w]
    problems += [f"two RunningWheels publish WheelDistanceSubjectName {w}" for w in sorted({w for w in wheels if w and wheels.count(w) > 1})]
    problems += [f"the workflow includes {path.split(':')[1].removesuffix('.bonsai')}, which a lab workflow does not use: each camera "
                 f"piece logs itself ({use})" for path, use in RETIRED_LOGS.items() if f'Path="{path}"' in wf.text]
    gate = paths.index(READY_GATE) if READY_GATE in paths else len(paths)
    if READ_LAB_RIG in paths[gate:]:
        problems.append("ReadLabRig comes after ReadyGate")
    problems += [f"{p.split(':')[1].removesuffix('.bonsai')} {props.get('Name') or props.get('Board') or ''}".rstrip()
                 + " comes before ReadyGate" for p, props in pieces[:gate] if p in PIECES]
    names = [props.get("Name", "") for p, props in pieces if "Name" in PIECES.get(p, ())]
    problems += [f"two pieces are named {n}" for n in sorted({n for n in names if names.count(n) > 1})]
    problems += [f"{p.split(':')[1].removesuffix('.bonsai').split('.')[-1]} {props.get('Name')} is not named {KINDS[p]}-<Instance>"
                 for p, props in pieces if p in KINDS and props.get("Name") and not props["Name"].startswith(KINDS[p] + "-")]
    boards = {props.get("Name") for p, props in pieces if p == BOARD}
    cameras = [props.get("Name", "") for p, props in pieces if p == CAMERA]
    camera_boards = {props.get("Name", ""): props.get("Board", "") for p, props in pieces if p == CAMERA}
    problems += [f"CameraLiveControls' Board {props.get('Board')} is not {props.get('Camera')}'s, {camera_boards[props['Camera']]}"
                 for p, props in pieces if p == CAMERA_LIVE_CONTROLS and props.get("Camera") in camera_boards
                 and props.get("Board") != camera_boards[props["Camera"]]]
    for p, props in pieces:
        piece = p.split(":")[1].removesuffix(".bonsai").split(".")[-1]
        for takes in PIECES.get(p, ()):
            device, _, suffix = takes.partition(".")
            if not suffix:
                value = props.get(takes, "")
                if not value:
                    problems.append(f"{piece} has no {takes}")
                elif takes == "Board" and value not in boards:
                    problems.append(f"{piece}'s Board {value} is not a BehaviorBoard piece's Name")
                elif takes == "Camera" and value not in cameras:
                    problems.append(f"{piece}'s Camera {value} is not a SpinnakerCamera piece's Name")
                continue
            prop = ("" if device == "Name" else device) + suffix + "SubjectName"
            want = props.get(device, "") + "." + suffix
            if props.get(prop) != want:
                problems.append(f"{piece} {props.get(device, '')}'s {prop} is {props.get(prop) or 'not set'}, not {want}")
    triggers = [props for p, props in pieces if p == CAMERA_TRIGGERS]
    if len(triggers) != (1 if cameras else 0):
        problems.append(f"the workflow has {len(cameras)} camera(s) and {len(triggers)} CameraTriggers: one when there is a camera, else none")
    elif triggers and set(_stages(triggers[0].get("CamerasExpected"))) != set(cameras):
        problems.append(f"CameraTriggers' CamerasExpected is {triggers[0].get('CamerasExpected') or 'not set'}, not {', '.join(cameras)}")
    if len(triggers) == 1:
        problems += [f"SpinnakerCamera {name}'s Board {board} is not CameraTriggers' Board {triggers[0].get('Board') or '(not set)'}"
                     for name, board in camera_boards.items() if board != triggers[0].get("Board")]
    always = [p for p, _, conditional in walked if not conditional]
    reads = [props for p, props in pieces if p == CLOCK_FROM_BOARD and props.get("SetClock", "").lower() == "false"]
    if reads and f'Path="{NO_EXPERIMENT_FOLDER}"' not in wf.text:
        problems.append("ClockFromBoard's SetClock is false, so the board's clock is never set: only a workflow with "
                        "NoExperimentFolder (one that records nothing) may read it unset")
    announced = list(dict.fromkeys(CLOCK_READ if p == CLOCK_FROM_BOARD and reads else READY_STAGES[p]
                                   for p in (*CLOCKS, PULSE_OUTPUTS, LICK_SPOUT_STAGE) if p in always)) \
        + [props.get("Name", "") for p, props, conditional in walked if p == CAMERA and not conditional] \
        + ([READY_STAGES[SPEAKER_MIXER]] if SPEAKER_MIXER in always else [])
    gate_props = next((props for p, props in pieces if p == READY_GATE), {})
    waits = gate_props.get("PieceStages")
    if READY_GATE in paths and set(_stages(waits)) != set(announced):
        problems.append(f"ReadyGate's PieceStages is {waits or 'not set'}, not {', '.join(announced)}")
    own = [child(n, W + "Combinator").findtext(CORE + "Stage") or "" for n in wf.root.iter(W + "Expression")
           if kind(n) == "core:AnnounceStage"]
    workflow_stages = gate_props.get("WorkflowStages")
    if READY_GATE in paths and set(_stages(workflow_stages)) != set(own):
        problems.append(f"ReadyGate's WorkflowStages is {workflow_stages or 'not set'}, not the stages the workflow announces "
                        f"itself ({', '.join(own) or 'none'})")
    problems += [f"the workflow has its own {what}" for what, found in (
        ("Ready report", re.search(r"<\w+:Phase>Ready</\w+:Phase>", wf.text)),
        ("StartCameras", "<Name>StartCameras</Name>" in wf.text)) if found]
    return problems + _rig_list_problems(wf) + ready_announcer_problems(wf.root) + shared_mapping_problems(wf.root)


def shared_mapping_problems(root: ET.Element) -> list[str]:
    """No PropertyMapping is fed by a node with another successor. Bonsai shares such a node's output once every
    successor has subscribed, and a PropertyMapping subscribes just before its target, so a value ready at Start (a
    completed AsyncSubject's) reaches the target only after it has read its properties: it keeps its XML default
    (SpinnakerCamera's Stage, 16 Nov 2026). Give each mapping its own source. Allowed: a node that feeds only the
    mapping and the mapping's own targets (the value comes with each element), and WorkflowInput (an include's input
    arrives from its caller). `root` is a workflow or a lab include."""
    problems = []
    for nodes, edges in _scopes(root):
        for i, node in enumerate(nodes):
            if node.get(XSI) != "PropertyMapping":
                continue
            targets = {b for a, b, _ in edges if a == i}
            for source in {a for a, b, _ in edges if b == i and nodes[a].get(XSI) not in ("ExternalizedMapping", "WorkflowInput")}:
                others = {b for a, b, _ in edges if a == source} - {i} - targets
                if others:
                    props = ", ".join(p.get("Name", "") for p in node.iter(W + "Property"))
                    problems.append(f"the PropertyMapping of {props} onto {', '.join(sorted(kind(nodes[t]).split(':')[-1] for t in targets))} "
                                    f"shares its source ({kind(nodes[source]).split(':')[-1]} {_name(nodes[source])}".rstrip()
                                    + ") with another node: give it its own")
    return problems


def ready_announcer_problems(root: ET.Element) -> list[str]:
    """Every multicast into ReadyStages is fed by an AnnounceStage alone: ReadyStages is a ReplaySubject, so an announcer
    that completes (a Take(1)) closes it for every stage after (seen headless, 15 Nov 2026). `root` is a workflow or a
    lab include."""
    problems = []
    for nodes, edges in _scopes(root):
        for i, node in enumerate(nodes):
            if node.get(XSI) == "MulticastSubject" and _name(node) == "ReadyStages":
                feeds = [nodes[a] for a, b, _ in edges if b == i]
                if len(feeds) != 1 or kind(feeds[0]) != "core:AnnounceStage":
                    problems.append("ReadyStages is fed other than by one AnnounceStage")
    return problems


def fixed_width_pulses_on_reward_valves(wf: Workflow, valve_lines: set[str]) -> list[str]:
    """Every fixed-width pulse the workflow may fire on a RewardValve line (`valve_lines`, rig.yml's, such as DO1, from
    LabRig.reward_valve_lines). A valve gives water only in pulses sized for it: a reward's by its RewardValve piece
    (LoadRewardLine, PlanReward), a calibration's by its own FirePulses, both on its board's TriggerCalibratedPulse,
    which writes each pulse's width before it sets the line. Refused: a board message that pulses, sets or sizes a
    valve's line itself (OutputSet, OutputPulseEnable, PulseDO<n>, PulseDOPort<n>)."""
    found = []
    for nodes, _ in _scopes(wf.root):
        for node in nodes:
            payload = next((e for e in node if e.tag.endswith("}Payload")), None)
            if payload is None:
                continue
            ptype = payload.get(XSI) or ""
            written = " ".join(payload.itertext())
            for line in sorted(valve_lines):
                if ptype.endswith(f":CreatePulse{line}Payload") or (
                        re.search(r":Create(OutputSet|OutputPulseEnable)Payload$", ptype) and re.search(rf"\b{line}\b", written)):
                    found.append(f"{_name(node) or ptype.split(':')[-1]} writes {line}, a RewardValve, itself")
    return found


def experiment_log_problems(wf: Workflow) -> list[str]:
    """The experiment's shared logs are OpenRigs.Logging's ExperimentLogs (folder, WorkflowData_Events, Settings_Rig,
    Settings_Experiment, Recording) and LogControlEvents (the controller's events, in ExperimentRecords or alone), each
    included once; the workflow builds none of them itself."""
    records = wf.text.count(f'Path="{EXPERIMENT_RECORDS}"')
    problems = [f"the workflow does not include {name} once" for name in ("ExperimentLogs", "LogControlEvents")
                if wf.text.count(f'Path="OpenRigs.Logging:{name}.bonsai"') + (records if name == "LogControlEvents" else 0) != 1]
    problems += [f"the workflow includes {name} itself (ExperimentLogs does)" for name in ("LogController", "LogEvents")
                 if f'Path="OpenRigs.Logging:{name}.bonsai"' in wf.text]
    problems += [f"the workflow logs {name} itself (ExperimentLogs does)" for name in ("Settings_Rig", "Settings_Experiment")
                 if f"<Name>{name}</Name>" in wf.text]
    # Every device's log waits for PathPrefix, and Bonsai builds a subject's whole top-level node when a subscriber comes
    # first: ExperimentLogs inside a group with a Timestamp reader, or after the pieces, is defined in terms of itself.
    top = _top(wf)[0]
    paths = [n.get("Path") for n in top]
    log = "OpenRigs.Logging:ExperimentLogs.bonsai"
    if log in wf.text and log not in paths:
        problems.append("ExperimentLogs is not a top-level node of its own")
    elif log in paths:
        first_piece = next((i for i, n in enumerate(top) for e in n.iter(W + "Expression")
                            if e.get("Path") in set(PIECES) | set(RIG_LISTS) | {READY_GATE}), None)
        if first_piece is not None and first_piece < paths.index(log):
            problems.append("ExperimentLogs comes after the hardware")
    return problems


def zapit_include_problems(wf: Workflow) -> list[str]:
    """Zapit is lib-open-rigs' ConnectForTask, included once (ConnectZapit given the task's conditions file in an
    optogenetics experiment), and nothing of its connect, check and refusal chain is built in the workflow, nor its
    subjects ZapitCommands and ZapitReplies declared."""
    problems = [] if wf.text.count(f'Path="{ZAPIT_FOR_TASK}"') == 1 else ["the workflow does not include ConnectForTask once"]
    if f'Path="{ZAPIT_INCLUDE}"' in wf.text:
        problems.append("the workflow includes ConnectZapit itself (ConnectForTask does)")
    kinds = {kind(n) for n in wf.root.iter(W + "Expression")}
    problems += [f"the workflow builds {local} itself" for local in ("zap:ZapitClient", "zap:CheckZapitConditionCount") if local in kinds]
    problems += [f"the workflow declares {name}, which ConnectZapit declares" for name in ("ZapitCommands", "ZapitReplies")
                 if _order(wf.root, _declares(name)) is not None]
    if _order(wf.root, _declares("Optogenetics")) is not None:
        problems.append("the workflow declares Optogenetics itself (ConnectForTask does)")
    if "zap:ZapitLineMessage" in kinds:
        problems.append("the workflow writes the ZapitTrigger line itself (SetZapitTriggerLine does)")
    commands = {props.get("Name", "") + ".Commands" for p, props in hardware(wf) if p == BOARD}
    for n in wf.root.iter(W + "Expression"):
        if n.get("Path") == SET_ZAPIT_TRIGGER_LINE:
            props = _properties(n)
            if "Set" not in props:
                problems.append("a SetZapitTriggerLine does not state Set")
            if props.get("BoardCommandsSubjectName") not in commands:
                problems.append(f"a SetZapitTriggerLine's BoardCommandsSubjectName is {props.get('BoardCommandsSubjectName') or 'not set'}, "
                                f"not a BehaviorBoard piece's <Name>.Commands ({', '.join(sorted(commands)) or 'no board'})")
    return problems


def _order(root: ET.Element, found) -> int | None:
    """The document position of the first node `found` accepts, None if none does."""
    return next((i for i, n in enumerate(root.iter(W + "Expression")) if found(n)), None)


def _declares(name: str):
    return lambda n: (n.get(XSI) or "").startswith("rx:") and (n.get(XSI) or "").endswith("Subject") and _name(n) == name


def spout_step_size_problems(wf: Workflow) -> list[str]:
    """SpoutStepSize is LickSpoutStage's: the piece declares it, takes it live and shows its default (150) at Start. The
    workflow handles it nowhere itself and sets it on no include; a declaration of its own is declared_twice's."""
    problems = []
    own = [c for c in wf.root.iter(W + "Combinator") if c.get(XSI, "").split(":")[-1] in ("OnParameterSet", "ParameterDefault")
           and c.findtext(CONTROL + "Name") == "SpoutStepSize"]
    if own or _order(wf.root, _declares("SpoutStepSize")) is not None:
        problems.append("the workflow handles SpoutStepSize itself (LickSpoutStage does)")
    if re.search(r'<SpoutStepSize>|Property Name="SpoutStepSize"', wf.text):
        problems.append("the workflow sets SpoutStepSize on an include, which takes it from the parameter")
    return problems


def background_noise_problems(wf: Workflow) -> list[str]:
    """Every task with sound has background noise: OpenRigs.Audio's BackgroundNoise, included once, after the task
    declares LaunchTaskConfig and TaskConfig, which it reads; the workflow sends nothing into BackgroundNoise and reads
    no backgroundNoise itself. The noise is the lab's one file, rig.yml's speakerArray.backgroundNoise (16 Nov 2026), so
    the include takes no file and the task keeps no noise WAV."""
    count = wf.text.count(f'Path="{BACKGROUND_NOISE}"')
    mixer = any(p == SPEAKER_MIXER for p, _ in hardware(wf))
    if count != (1 if mixer else 0):
        return [f"the workflow includes BackgroundNoise {count} times, " + ("not once" if mixer else "and has no SpeakerMixer")]
    problems = []
    if any(n.get("Path") == BACKGROUND_NOISE and n.find(W + "FileName") is not None for n in wf.root.iter(W + "Expression")):
        problems.append("the workflow gives BackgroundNoise a FileName (the noise is rig.yml's speakerArray.backgroundNoise)")
    for wav in sorted(p for p in (wf.repo / "src").rglob("*.wav") if "noise" in p.name.lower()):
        problems.append(f"{wav.relative_to(wf.repo).as_posix()} is a noise of the task's own (BackgroundNoise plays rig.yml's speakerArray.backgroundNoise)")
    if any(n.get(XSI) == "MulticastSubject" and _name(n) == "BackgroundNoise" for n in wf.root.iter(W + "Expression")):
        problems.append("the workflow sends BackgroundNoise itself (the BackgroundNoise include does)")
    if "TaskParameters.BackgroundNoise" in wf.text:
        problems.append("the workflow reads the task file's backgroundNoise itself (BackgroundNoise does)")
    if count:
        at = _order(wf.root, lambda n: n.get("Path") == BACKGROUND_NOISE)
        for name in ("LaunchTaskConfig", "TaskConfig"):
            declared = _order(wf.root, _declares(name))
            if declared is None or at is None or declared > at:
                problems.append(f"BackgroundNoise comes before {name} is declared")
    return problems


def experiment_records_problems(wf: Workflow) -> list[str]:
    """The experiment's records are OpenRigs.Logging's ExperimentRecords (LogControlEvents, RecordHarpDevices,
    ExperimentManifest), included once with ConfigRoot, IngestScript and ExpectedFiles set, as the last top-level node
    but StreamDevices: RecordHarpDevices and ExperimentManifest find each device by the subjects published before them.
    The workflow has none of the three of its own. Rewards are not records: Rewards is the workflow's own include."""
    count = wf.text.count(f'Path="{EXPERIMENT_RECORDS}"')
    if count != 1:
        return [f"the workflow includes ExperimentRecords {count} times, not once"]
    problems = [f"the workflow has its own {name} (ExperimentRecords does)" for name in ("RecordHarpDevices", "ExperimentManifest")
                if any(kind(n).endswith(":" + name) for n in wf.root.iter(W + "Expression"))]
    if 'Path="OpenRigs.Logging:LogControlEvents.bonsai"' in wf.text:
        problems.append("the workflow has its own LogControlEvents (ExperimentRecords does)")
    top = _top(wf)[0]
    at = next((i for i, n in enumerate(top) if n.get("Path") == EXPERIMENT_RECORDS), None)
    if at is None or any(not kind(n).endswith(":StreamDevices") for n in top[at + 1:]):
        problems.append("ExperimentRecords is not the last top-level node (StreamDevices alone may follow)")
    else:
        props = _properties(top[at])
        problems += [f"ExperimentRecords has no {p}" for p in ("ConfigRoot", "IngestScript", "ExpectedFiles") if not props.get(p)]
    return problems


# Device sources only a lab include opens, (CLR namespace, type): the piece that opens each. A Harp board (a Device in a
# Harp.* namespace, Bonsai.Harp or LicketySplit's) is its kind's piece too. A workflow that opens one itself skips the
# piece's checks, log and Ready stage.
PIECED_SOURCES = {("Bonsai.Spinnaker", "SpinnakerCapture"): "SpinnakerCamera", ("OpenRigs.Video", "TriggeredSpinnaker"): "SpinnakerCamera",
                  ("OpenRigs.Video", "UsbCamera"): "UsbCamera", ("Bonsai.Shaders", "CreateWindow"): "ProjectorDisplay or RawProjectorDisplay",
                  # A serial port, opened by name (Bonsai.System's SerialPortManager) by any of these: a balance is
                  # SerialBalance's, every other serial device its kind's piece.
                  **{("Bonsai.IO.Ports", t): "SerialBalance, or the device's piece" for t in ("CreateSerialPort", "SerialReadLine", "SerialStringRead")}}


def hand_opened_devices(wf: Workflow) -> list[str]:
    """Every device source the workflow, or a file of its own it includes, opens itself rather than through the lab's
    piece for it (PIECED_SOURCES, and any Harp board's Device), as "<type> (<piece>)"."""
    found = []
    for text in wf.texts:
        namespaces = dict(re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+);', text))
        for prefix, local in re.findall(r'xsi:type="(\w+):(\w+)"', text):
            ns = namespaces.get(prefix, "")
            harp = local == "Device" and (ns.startswith("Harp.") or ns in ("Bonsai.Harp", "AllenNeuralDynamics.LicketySplit"))
            piece = "the board's piece" if harp else PIECED_SOURCES.get((ns, local))
            if piece:
                found.append(f"{ns}.{local} ({piece})")
    return found


def _top(wf: Workflow) -> tuple[list[ET.Element], list[tuple[int, int, str]]]:
    """The workflow's top-level nodes and edges, each own include with no edge into or out of it replaced in place by
    its own top level: Bonsai builds an include's nodes where the include stands, so a thin entry file's shared include
    (the job and the live view) is the top level too."""
    def flat(workflow: ET.Element) -> tuple[list[ET.Element], list[tuple[int, int, str]]]:
        nodes, edges = nodes_and_edges(workflow)
        out, out_edges, at = [], [], {}
        for j, node in enumerate(nodes):
            inner = node.find(W + "Workflow")
            if node.get(XSI) == "IncludeWorkflow" and inner is not None and not any(j in e[:2] for e in edges):
                inner_nodes, inner_edges = flat(inner)
                out_edges += [(a + len(out), b + len(out), label) for a, b, label in inner_edges]
                out += inner_nodes
            else:
                at[j] = len(out)
                out.append(node)
        return out, out_edges + [(at[a], at[b], label) for a, b, label in edges]

    return flat(child(wf.root, W + "Workflow"))


def live_view_problems(wf: Workflow) -> list[str]:
    """The live view is one OpenRigs.LiveView StreamDevices, the workflow's last top-level node (_top: a thin entry's
    shared include may hold it), with no edge into or out of it: it streams only the subjects built before it, and an
    edge into it builds it before them, so it streams nothing (a task shipped that way)."""
    nodes, edges = _top(wf)
    live = [i for i, n in enumerate(nodes) if kind(n).endswith(":StreamDevices")]
    if len(live) != 1 or len(list(e for e in wf.root.iter(W + "Expression") if kind(e).endswith(":StreamDevices"))) != 1:
        return ["the workflow does not have one StreamDevices, at its top level"]
    problems = [] if live[0] == len(nodes) - 1 else ["StreamDevices is not the last top-level node"]
    return problems + (["StreamDevices has an edge into or out of it"] if any(live[0] in e[:2] for e in edges) else [])


def reward_problems(wf: Workflow) -> list[str]:
    """Every reward is OpenRigs.Devices' Rewards' and one RewardValve piece's per valve:
    Rewards declares RewardRequests, RewardValves and RewardDelivered, refuses a reward for a valve no piece fires and
    logs each delivery; each valve's piece loads its calibration, gives its own manual drop (<Name>.Reward) and fires the
    rewards asked of it. A workflow that sends a reward (a multicast into RewardRequests) or has a valve includes Rewards
    once, before it sends any, before every valve and before ExperimentRecords (rewards are not records), and has at
    least one valve. No workflow declares or reads RewardRequests itself, or sends into RewardValves or RewardDelivered:
    RewardRequests is a BehaviorSubject (a per-trial sender's completion would close a PublishSubject), so a reader
    subscribed per trial would be handed the last reward again. A rig without reward valves (the behaviour box) includes
    none."""
    count = wf.text.count(f'Path="{REWARDS}"')
    sent = _order(wf.root, lambda n: n.get(XSI) == "MulticastSubject" and _name(n) == "RewardRequests")
    valves = _order(wf.root, lambda n: n.get("Path") == REWARD_VALVE)
    problems = [f"the workflow includes Rewards {count} times, not once"] if count > 1 or ((sent, valves) != (None, None) and count != 1) else []
    if count and valves is None:
        problems.append("the workflow includes Rewards and no RewardValve piece, so no reward can be given")
    nodes = list(wf.root.iter(W + "Expression"))
    if any(_declares("RewardRequests")(n) for n in nodes):
        problems.append("the workflow declares RewardRequests itself (Rewards does)")
    if any(n.get(XSI) == "SubscribeSubject" and _name(n) == "RewardRequests" for n in nodes):
        problems.append("the workflow reads RewardRequests itself (each RewardValve piece delivers its rewards)")
    problems += [f"the workflow sends into {name} itself (Rewards and the RewardValve pieces do)" for name in ("RewardValves", "RewardDelivered")
                 if any(n.get(XSI) == "MulticastSubject" and _name(n) == name for n in nodes)]
    at = _order(wf.root, lambda n: n.get("Path") == REWARDS)
    records = _order(wf.root, lambda n: n.get("Path") == EXPERIMENT_RECORDS)
    if at is not None and sent is not None and sent < at:
        problems.append("Rewards comes after a reward is sent into RewardRequests")
    if at is not None and valves is not None and valves < at:
        problems.append("Rewards comes after a RewardValve piece")
    if at is not None and records is not None and at > records:
        problems.append("Rewards comes after ExperimentRecords")
    return problems


def launch_settings_problems(wf: Workflow) -> list[str]:
    """SubjectId, TaskPath and ExperimentIds are OpenRigs.Control's LaunchSettings, included once after
    ExperimentControl (whose ExperimentGo it reads), with the task file's TaskPath; the workflow declares none itself."""
    count = wf.text.count(f'Path="{LAUNCH_SETTINGS}"')
    if count != 1:
        return [f"the workflow includes LaunchSettings {count} times, not once"]
    problems = [f"the workflow declares {name} itself (LaunchSettings does)" for name in ("SubjectId", "TaskPath", "ExperimentIds")
                if _order(wf.root, _declares(name)) is not None]
    at = _order(wf.root, lambda n: n.get("Path") == LAUNCH_SETTINGS)
    control = _order(wf.root, lambda n: n.get("Path") == EXPERIMENT_CONTROL)
    if control is None or at is None or control > at:
        problems.append("LaunchSettings comes before ExperimentControl")
    node = next(n for n in wf.root.iter(W + "Expression") if n.get("Path") == LAUNCH_SETTINGS)
    if not _properties(node).get("TaskPath"):
        problems.append("LaunchSettings has no TaskPath (the task file run from the editor)")
    return problems


def launch_id_problems(wf: Workflow) -> list[str]:
    """A workflow with ExperimentControl exposes LaunchId at its top level, mapped down through each group to
    ExperimentControl's LaunchId: a launcher passes --property:LaunchId=<run> only to a workflow that exposes it,
    and after a crash finds the experiment folder by the manifest's control.launch_id."""
    def mapped(nodes: list[ET.Element], edges: list[tuple[int, int, str]], j: int) -> bool:
        return any(p.get("Name") == "LaunchId" and (p.get("DisplayName") or "LaunchId") == "LaunchId"
                   for a, b, _ in edges if b == j and nodes[a].get(XSI) == "ExternalizedMapping" for p in nodes[a].iter(W + "Property"))

    def walk(workflow: ET.Element) -> list[str]:
        nodes, edges = nodes_and_edges(workflow)
        for j, node in enumerate(nodes):
            if not any(e.get("Path") == EXPERIMENT_CONTROL for e in node.iter(W + "Expression")):
                continue
            if not mapped(nodes, edges, j):
                where = ("ExperimentControl" if node.get("Path") == EXPERIMENT_CONTROL else f"the include {node.get('Path')}"
                         if node.get(XSI) == "IncludeWorkflow" else f"the group {_name(node) or kind(node)}")
                return [f"{where} is not given LaunchId by an ExternalizedMapping (map the top-level LaunchId down to ExperimentControl)"]
            inner = node.find(W + "Workflow")
            return walk(inner) if inner is not None and node.get("Path") != EXPERIMENT_CONTROL else []
        return []

    return walk(child(wf.root, W + "Workflow"))


def no_experiment_folder_problems(wf: Workflow) -> list[str]:
    """The workflow makes the experiment folder (OpenRigs.Logging's ExperimentLogs, with LaunchSettings) or makes none
    (NoExperimentFolder: a calibration, a preflight), one of the two once, and stands in for neither itself: no own
    PathPrefix declared or published, and with NoExperimentFolder no LaunchSettings, no own SubjectId (in its own
    included files too), and its SubjectId left at NoExperimentFolder's own, NO_EXPERIMENT_SUBJECT."""
    log, none = (wf.text.count(f'Path="{path}"') for path in (LOG_LAB_EXPERIMENT, NO_EXPERIMENT_FOLDER))
    if log + none != 1:
        return [f"the workflow includes ExperimentLogs {log} and NoExperimentFolder {none} times: one of the two once"]
    nodes = list(wf.root.iter(W + "Expression"))
    problems = [f"the workflow {what} {name} itself" for what, name, found in (
        ("declares", "PathPrefix", _declares("PathPrefix")),
        ("publishes", "PathPrefix", lambda n: n.get(XSI) == "MulticastSubject" and _name(n) == "PathPrefix"),
        ("declares", "SubjectId", _declares("SubjectId") if none else lambda n: False)) if any(map(found, nodes))]
    if none:
        if f'Path="{LAUNCH_SETTINGS}"' in wf.text:
            problems.append("the workflow includes LaunchSettings and NoExperimentFolder (both declare SubjectId)")
        node = next(n for n in wf.root.iter(W + "Expression") if n.get("Path") == NO_EXPERIMENT_FOLDER)
        subject = _properties(node).get("SubjectId", NO_EXPERIMENT_SUBJECT)
        if subject != NO_EXPERIMENT_SUBJECT:
            problems.append(f"NoExperimentFolder's SubjectId is {subject or 'empty'}, not its own {NO_EXPERIMENT_SUBJECT}: "
                            "delete the workflow's value")
    return problems


def preflight_tap_problems(wf: Workflow) -> list[str]:
    """A preflight (it includes PreflightChecks) taps its devices through its rig list's taps include (RIG_LIST_TAPS),
    as often as the rig list, and has no PreflightTap of its own (in its own included files too)."""
    if f'Path="{PREFLIGHT_CHECKS}"' not in wf.text:
        return []
    problems = []
    for rig_list, taps in RIG_LIST_TAPS.items():
        lists, tapped = wf.text.count(f'Path="{rig_list}"'), wf.text.count(f'Path="{taps}"')
        if lists != tapped:
            names = [p.split(":")[1].removesuffix(".bonsai") for p in (rig_list, taps)]
            problems.append(f"the preflight includes {names[0]} {lists} and {names[1]} {tapped} times: the taps once with their rig list")
    if any(re.search(r'xsi:type="\w+:PreflightTap"', text) for text in wf.texts):
        problems.append("the preflight taps a device itself (PreflightTap): its rig list's taps include taps every device")
    return problems


def penalty_sound_problems(wf: Workflow) -> list[str]:
    """The penalty sound is OpenRigs.Audio's PlayPenaltySound, included once: the workflow makes no PenaltySoundBuffer,
    sends nothing into PenaltySounds and reads no penalty sound file itself."""
    count = wf.text.count(f'Path="{PLAY_PENALTY_SOUND}"')
    problems = [] if count == 1 else [f"the workflow includes PlayPenaltySound {count} times, not once"]
    nodes = list(wf.root.iter(W + "Expression"))
    if any(kind(n) == "aud:PenaltySoundBuffer" for n in nodes):
        problems.append("the workflow makes its own PenaltySoundBuffer (PlayPenaltySound does)")
    if any(n.get(XSI) == "MulticastSubject" and _name(n) == "PenaltySounds" for n in nodes):
        problems.append("the workflow sends PenaltySounds itself (PlayPenaltySound does)")
    return problems + (["the workflow reads its own penalty sound file"] if "Sound.Path" in wf.text else [])


def readme_missing_blocks(wf: Workflow) -> list[str]:
    """Every OpenRigs operator and include the workflow uses that its README's Shared Blocks table does not name."""
    readme = (wf.repo / "README.md").read_text(encoding="utf-8")
    table = readme[readme.index("## Shared Blocks"):]
    table = table[: table.index("\n## ", 4)] if "\n## " in table[4:] else table
    prefixes = [p for p, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+);', wf.text) if ns.startswith("OpenRigs")]
    used = {name for p in prefixes for name in re.findall(r'xsi:type="' + p + r':(\w+)"', wf.text)}
    used |= {p.split(":")[1].replace(".bonsai", "").split(".")[-1] for p in re.findall(r'Path="(OpenRigs\.[^"]+)"', wf.text)}
    return sorted(b for b in used if f"`{b}`" not in table)


def _on_commands(root: ET.Element) -> set[str]:
    # From the tree: ET.tostring writes its own prefixes (ns2:Name), so a regex on <orcl:Name> found none.
    names = (c.findtext(CONTROL + "Name") for c in root.iter(W + "Combinator") if (c.get(XSI) or "").endswith(":OnCommand"))
    return {n for n in names if n}


def _readable(path: str) -> bool:
    """Whether package_include can read the lab include `path`: from lib-open-rigs' source, or by its signature."""
    package, resource = path.split(":")
    return path in SIGNATURES or (LIB_SRC / package / resource.replace(".", "/", resource.count(".") - 1)).is_file()


def included_roots(wf: Workflow) -> list[ET.Element]:
    """The workflow, each workflow file of its own it includes, and each lab include they reach that package_include
    reads (every one that declares controls: test_every_declaring_piece_is_readable), at any depth, once per include
    node: a piece included twice is two pieces."""
    roots, todo = [], [wf.root]
    while todo:
        root = todo.pop(0)
        roots.append(root)
        for n in root.iter(W + "Expression"):
            included = n.get("Path") or ""
            if n.get(XSI) == "IncludeWorkflow" and included.startswith("OpenRigs.") and ":" in included and _readable(included):
                todo.append(package_include(wf, included))
    return roots


def control_declarations(wf: Workflow) -> list[tuple[str, str, str]]:
    """Every parameter and command the running workflow would declare, as (declarer, "parameter" or "command",
    name): ExperimentControl's own (declarer "ExperimentControl"), each DeclareControls in the pieces it includes
    (declarer its Piece) and each device declarer's (DEVICE_DECLARERS: DeclareDeviceControls, declarer its device, each
    name <device>.<name>), as ControlServer gathers them."""
    found = []
    for root in included_roots(wf):
        for n in root.iter(W + "Expression"):
            if n.get("Path") == EXPERIMENT_CONTROL:
                found += [("ExperimentControl", kind, e.findtext(W + "Name") or "") for kind, tag in (("parameter", "Parameters/" + W + "Parameter"), ("command", "Commands/" + W + "Command"))
                          for e in n.iterfind(W + tag)]
        for c in root.iter(W + "Combinator"):
            if (c.get(XSI) or "").endswith(":DeclareControls"):
                piece = c.findtext(CONTROL + "Piece") or ""
                found += [(piece, kind, e.findtext(CONTROL + "Name") or "") for kind, tag in (("parameter", "Parameters/" + CONTROL + "Parameter"), ("command", "Commands/" + CONTROL + "Command"))
                          for e in c.iterfind(CONTROL + tag)]
    for path, props in hardware(wf):
        if path in DEVICE_DECLARERS:
            device = props.get(DEVICE_DECLARERS[path], "")
            for c in package_include(wf, path).iter(W + "Combinator"):
                if (c.get(XSI) or "").endswith(":DeclareDeviceControls"):
                    found += [(device, kind, f"{device}.{e.findtext(CONTROL + 'Name') or ''}") for kind, tag in (("parameter", "Parameters/" + CONTROL + "Parameter"), ("command", "Commands/" + CONTROL + "Command"))
                              for e in c.iterfind(CONTROL + tag)]
    return found


def declared_twice(wf: Workflow) -> list[str]:
    """Each name two declarers declare (a task's copy of a piece's command, two of one piece): ControlServer stops
    the workflow at Start naming both."""
    by: dict[tuple[str, str], list[str]] = {}
    for declarer, kind, name in control_declarations(wf):
        by.setdefault((kind, name), []).append(declarer)
    return [f"the {kind} {name} is declared by {' and by '.join(who)}" for (kind, name), who in by.items() if len(who) > 1]


def undeclared_commands(wf: Workflow) -> list[str]:
    """Every command the workflow or a piece it includes handles (OnCommand) that neither its ExperimentControl nor a
    piece declares: ControlServer refuses an undeclared command, so it would never fire."""
    declared = {name for _, kind, name in control_declarations(wf) if kind == "command"}
    handled = set().union(*(_on_commands(root) for root in included_roots(wf)))
    return sorted(handled - declared)


def layout_problems(wf: Workflow) -> list[str]:
    """A tracked config\\main.layout, which .bonsai\\Setup.ps1 puts in place of a stale per-machine one, itself or
    through the lab's one copy of the setup (open_rigs\\scripts\\setup-bonsai.ps1, which it runs)."""
    problems = []
    layout = wf.repo / "config" / "main.layout"
    if not layout.is_file() or layout.read_text(encoding="utf-8").count("<VisualizerLayout") != 1:
        problems.append("config\\main.layout is not one VisualizerLayout")
    setup = (wf.repo / ".bonsai" / "Setup.ps1").read_text(encoding="utf-8-sig")
    if SETUP_BONSAI_RUN in setup:
        setup = SETUP_BONSAI.read_text(encoding="utf-8-sig")
    if "..\\config\\main.layout" not in setup:
        problems.append(".bonsai\\Setup.ps1 does not copy config\\main.layout")
    return problems


def rng_seed_problems(wf: Workflow) -> list[str]:
    """The RngSeed event is OpenRigs.Logging's LogRngSeed, included once at TaskLogic's top level (each task gives it
    its trials' seed from their start), and the workflow makes no RngSeed event itself."""
    nodes = scope(wf.named("TaskLogic"))[0]
    here = sum(n.get("Path") == LOG_RNG_SEED for n in nodes)
    count = wf.text.count(f'Path="{LOG_RNG_SEED}"')
    problems = [] if here == count == 1 else [f"LogRngSeed is included {count} times, {here} at TaskLogic's top level; it is once, there"]
    if any(c.findtext("{clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging}Event") == "RngSeed" for c in wf.root.iter(W + "Combinator")):
        problems.append("the workflow makes its own RngSeed event (LogRngSeed does)")
    return problems


def task_logs_problems(wf: Workflow) -> list[str]:
    """A task's trial log, Settings_Task and Zapit's logs are OpenRigs.Logging's TaskLogs, included once and given the
    task file as JSON; the workflow includes no LogTrials or ZapitLogs and logs no Settings_Task itself."""
    count = wf.text.count(f'Path="{TASK_LOGS}"')
    problems = [] if count == 1 else [f"the workflow includes TaskLogs {count} times, not once"]
    problems += [f"the workflow includes {path.split(':')[1].removesuffix('.bonsai').split('.')[-1]} itself (TaskLogs does)"
                 for path in ("OpenRigs.Logging:LogTrials.bonsai", "OpenRigs.Devices:ZapitLogs.bonsai") if f'Path="{path}"' in wf.text]
    if "<Name>Settings_Task</Name>" in wf.text:
        problems.append("the workflow logs Settings_Task itself (TaskLogs does)")
    for nodes, edges in _scopes(wf.root):
        for i, n in enumerate(nodes):
            if n.get("Path") == TASK_LOGS and not any(kind(nodes[a]).endswith(":SerializeToJson") for a, b, _ in edges if b == i):
                problems.append("TaskLogs is not given the task file as JSON (the task's SerializeToJson)")
    return problems


def output_ends_logs_problems(root: ET.Element) -> list[str]:
    """No scope's WorkflowOutput is fed by a one-shot settings write (LogDataSchema) while a branch beside it logs: the
    scope ends its side branches when its output completes, so the write that completes at Go ended the logs (no trial rows). `root` is an include's or a workflow's."""
    problems = []
    for nodes, edges in _scopes(root):
        for out in (i for i, n in enumerate(nodes) if n.get(XSI) == "WorkflowOutput"):
            feed = upstream(edges, out) | {out}
            if not any(nodes[i].get("Path") == "OpenRigs.Logging:LogDataSchema.bonsai" for i in feed):
                continue
            problems += [f"{n.findtext(W + 'Name') or kind(n)} logs beside a WorkflowOutput fed by a one-shot LogDataSchema, "
                         "which ends it when the write completes" for i, n in enumerate(nodes)
                         if i not in feed and any("Log" in (e.get("Path") or "") for e in n.iter(W + "Expression"))]
    return problems


def training_stage_problems(wf: Workflow) -> list[str]:
    """The training stage is OpenRigs.Control's TrainingStage, included once after ExperimentControl, which declares
    the parameter TrainingStage; the workflow reads the parameter, declares the subject and reports the metric nowhere
    itself."""
    count = wf.text.count(f'Path="{TRAINING_STAGE}"')
    if count != 1:
        return [f"the workflow includes TrainingStage {count} times, not once"]
    problems = [] if re.search(r"<Parameter>\s*<Name>TrainingStage</Name>", wf.text) else [
        "ExperimentControl does not declare the parameter TrainingStage, which TrainingStage reads"]
    if _order(wf.root, _declares("TrainingStage")) is not None:
        problems.append("the workflow declares TrainingStage itself (TrainingStage does)")
    names = {c.findtext(CONTROL + "Name") or "" for c in wf.root.iter(W + "Combinator")
             if (c.get(XSI) or "").split(":")[-1] in ("ParameterValue", "ReportMetric")}
    problems += [f"the workflow handles {name} itself (TrainingStage does)" for name in sorted(names & {"TrainingStage", "trainingStage"})]
    at = _order(wf.root, lambda n: n.get("Path") == TRAINING_STAGE)
    control = _order(wf.root, lambda n: n.get("Path") == EXPERIMENT_CONTROL)
    if control is None or at is None or control > at:
        problems.append("TrainingStage comes before ExperimentControl")
    return problems


# The blocks that write an experiment's files, and what each writes. A log's file is FormatFileName's
# <LogName up to the first _>/<LogName>_<time>.<ext>, so the start of its path is known from its name.
NAMED_LOGS = ("OpenRigs.Logging:LogData.bonsai", "OpenRigs.Logging:LogWorkflowData.bonsai",
              "OpenRigs.Logging:LogDataSchema.bonsai")  # the name is the node's LogName or Name
FIXED_LOGS = {  # block: [(log name, the bool subject that decides whether it is written, or None)]
    "OpenRigs.Logging:LogTrials.bonsai": [("WorkflowData_Trials", None)],
    "OpenRigs.Logging:LogEvents.bonsai": [("WorkflowData_Events", None)],
    "OpenRigs.Logging:ExperimentLogs.bonsai": [("Settings_Rig", None), ("Settings_Experiment", None), ("WorkflowData_Events", None)],
    "OpenRigs.Devices:ZapitLogs.bonsai": [("Settings_ZapitConditions", "Optogenetics")],  # its replies are Events rows
}
FIXED_LOGS[TASK_LOGS] = [("Settings_Task", None), *FIXED_LOGS["OpenRigs.Logging:LogTrials.bonsai"], *FIXED_LOGS["OpenRigs.Devices:ZapitLogs.bonsai"]]
DEVICE_LOGS = ("OpenRigs.Logging:LogHarpDevice.bonsai", "OpenRigs.Logging:LogHarp.bonsai", "OpenRigs.Logging:LogHarpDemux.bonsai",
               "OpenRigs.Logging:LogVideoFfmpeg.bonsai", "OpenRigs.Logging:LogVideo.bonsai", "OpenRigs.Logging:LogRawVideo.bonsai",
               *DEVICE_GROUPS, LICK_SPOUT_STAGE, REWARD_VALVE, *RIG_LISTS)  # a device's folder: ExpectedFiles' auto (each piece logs itself)
NO_FILES = ("OpenRigs.Logging:LogController.bonsai", "OpenRigs.Logging:FormatFileName.bonsai", "OpenRigs.Logging:LogControlEvents.bonsai",
            LOG_RNG_SEED)
# Writers whose file the workflow's XML cannot name: a workflow logs through the blocks above instead.
OTHER_WRITERS = {"CsvWriter", "MatrixWriter", "VideoWriter", "ImageWriter", "TextWriter", "BinaryWriter", "FileCapture",
                 "MessageWriter", "ObjectWriter", "JsonWriter", "BinaryLogWriter", "FfmpegVideoWriter"}


def log_prefix(name: str) -> str:
    """The start of a log's path in the experiment folder, as FormatFileName makes it."""
    return name.split("_", 1)[0] + "/" + name + "_"


def expected_files_problems(wf: Workflow) -> list[str]:
    """Every file the workflow writes is named in ExperimentManifest's ExpectedFiles, one entry per log
    (the start of its path, not its folder: a folder is met by any one file in it), with the condition
    its block writes it under (path?Subject); every named entry is a file the workflow writes; device
    logs need auto and RecordHarpDevices needs harp. ExperimentRecords' ExpectedFiles is its manifest's (blank by
    default) and it records the Harp devices. Empty without an ExperimentManifest."""
    manifest = next((e for e in wf.root.iter(W + "Expression") if (e.get(XSI) or "").endswith(":ExperimentManifest")
                     or e.get("Path") == EXPERIMENT_RECORDS), None)
    if manifest is None:
        return []
    raw = next((c.text or "" for c in manifest if c.tag.endswith("}ExpectedFiles")), "" if manifest.get("Path") else "auto")
    entries = {}
    for item in (e.strip() for e in raw.split(";")):
        if item:
            path, _, condition = item.partition("?")
            entries[path.strip()] = condition.strip() or None
    writes, problems, devices, harp = {}, [], False, False
    for node in wf.root.iter(W + "Expression"):
        include = node.get("Path") or ""
        local = kind(node).split(":")[-1]
        if include in NAMED_LOGS:
            name = next((c.text for c in node if c.tag in (W + "LogName", W + "Name") and c.text), None)
            if name is None:
                problems.append(f"{include} has no LogName written in the workflow, so the file it writes cannot be named")
            else:
                writes[log_prefix(name)] = None
        elif include in FIXED_LOGS:
            writes.update({log_prefix(name): condition for name, condition in FIXED_LOGS[include]})
        elif include in DEVICE_LOGS:
            devices = True
        elif local == "RecordHarpDevices" or include == EXPERIMENT_RECORDS:
            harp = True
        elif include.startswith("OpenRigs.Logging:") and include not in NO_FILES:
            problems.append(f"{include} writes files this check does not know: add it to workflow_lint's writers")
        elif local in OTHER_WRITERS:
            problems.append(f"a {local} node writes a file the workflow's XML cannot name: log through OpenRigs.Logging's LogData")
    for prefix, condition in writes.items():
        entry = prefix + ("?" + condition if condition else "")
        if prefix not in entries:
            folder = prefix.split("/", 1)[0] + "/"
            problems.append(f"{entry} is written but not in ExpectedFiles" + (f" (name it, not {folder})" if folder in entries else ""))
        elif entries[prefix] != condition:
            problems.append(f"ExpectedFiles names {prefix}" + (f"?{entries[prefix]}" if entries[prefix] else "") + f"; its block writes it as {entry}")
    for path in entries:
        if path in ("auto", "harp") or path in writes:
            continue
        problems.append(f"ExpectedFiles names {path}, which no node in the workflow writes" + (" (name each log instead)" if path.endswith("/") else ""))
    if devices and "auto" not in entries:
        problems.append("the workflow logs devices but ExpectedFiles has no auto")
    if harp and "harp" not in entries:
        problems.append("the workflow has RecordHarpDevices but ExpectedFiles has no harp")
    return problems


TEMPLATE = Path(__file__).parent / "rig-template.yml"


def filled_rig(wf: Workflow, root: PurePath, machine: str) -> tuple[str, list[str], list[str]]:
    """The lab's rig template filled in as a rig with exactly the workflow's devices (required_devices), for
    check-workflow's build: (rig.yml's text, each Harp record RecordHarpDevices should make as "<Name> <port> <serial>
    <WhoAmI>", each device folder and Harp record file ExperimentManifest should expect). An entry the template lacks
    (a board named BehaviorBoard-Arena) is the template's first of its kind, as every camera's is the first camera's, and
    the board's DO0 trigger line names every camera (none: no DO0 line)."""
    from ruamel.yaml import YAML

    yaml = YAML()
    rig = yaml.load(TEMPLATE.read_text(encoding="utf-8"))
    devices = required_devices(wf)
    rig["rootPath"], rig["rigId"] = str(root), "D1"
    camera = dict(next(iter(rig["camera"].values())), serialNumber="1")
    entry = {name: name.split("-", 1)[1][:1].lower() + name.split("-", 1)[1][1:] for name, _ in devices}
    for group in PREFLIGHT_DEVICE_KINDS:
        wanted = [entry[name] for name, g in devices if g == group]
        if not wanted:
            del rig[group]
        elif group == "camera":
            rig[group] = {e: dict(camera) for e in wanted}
        else:
            first = copy.deepcopy(next(iter(rig[group].values())))
            for e in [e for e in rig[group] if e not in wanted]:
                del rig[group][e]
            for e in [e for e in wanted if e not in rig[group]]:
                rig[group][e] = copy.deepcopy(first)
    cameras = [name for name, g in devices if g == "camera"]
    for board in rig.get("behaviorBoard", {}).values():
        if cameras:
            board["wiring"]["DO0"]["targets"] = cameras
        else:
            del board["wiring"]["DO0"]
    records, files = [], []
    for n, (name, group) in enumerate(devices):
        files.append(f"{name}/")
        if group in WHO_AM_I:
            settings = rig[group][entry[name]]
            settings["usbSerial"] = f"SERIAL{n}"
            records.append(f"{name} {settings['portName']} SERIAL{n} {WHO_AM_I[group]}")
            files += [f"{name}/{name}_device.json", f"{name}/{name}.yml"]
    out = io.StringIO()
    yaml.dump(rig, out)
    return out.getvalue(), sorted(records), files


def main() -> None:
    """check-workflow's step 3: `python -m open_rigs.rigs.workflow_lint <repo> <config root> <machine> [<workflow>]` writes
    the filled rig.yml for src\\<workflow> (main.bonsai unless named) at <config root>\\<machine>\\rig.yml and prints what
    the build should make, with what `includes` says of the workflow, as JSON."""
    repo, config_root, machine = sys.argv[1:4]
    wf = Workflow(Path(repo), sys.argv[4] if len(sys.argv) > 4 else "main.bonsai")
    problems = hardware_problems(wf) + declared_twice(wf)
    text, records, files = filled_rig(wf, Path(config_root).parent, machine)
    folder = Path(config_root) / machine
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "rig.yml").write_text(text, encoding="utf-8")
    print(json.dumps({"problems": problems, "records": records, "files": files, **includes(wf)}))


def includes(wf: Workflow) -> dict[str, bool]:
    """What check-workflow asks of the workflow with its own included files: records_nothing (it includes
    NoExperimentFolder) and logs_trials (it includes LogTrials)."""
    return {"records_nothing": f'Path="{NO_EXPERIMENT_FOLDER}"' in wf.text,
            "logs_trials": 'Path="OpenRigs.Logging:LogTrials.bonsai"' in wf.text}


if __name__ == "__main__":
    main()
