"""The lab's rules for a workflow's src\\main.bonsai, read from its XML (standard library only).

Each task's tests\\test_workflow.py calls these on its own workflow, so a rule found in one task holds in every task:
    from open_rigs.rigs import workflow_lint as lint
    WORKFLOW = lint.Workflow(Path(__file__).parents[1])
    def test_no_trial_graph_closes_a_shared_subject():
        assert lint.subjects_closed_by_a_nested_multicast(WORKFLOW) == []
Each check returns what breaks the rule (empty when it holds), so a failing test names it.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
RX = "{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}"
SCRIPT = "{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}"
AUDIO = "{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}"
FINITE = ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")
WALL_CLOCK = ("rx:Timer", "rx:Delay", "rx:DelaySubscription", "rx:Throttle", "rx:Timeout", "rx:Interval", "rx:TimeInterval",
              "rx:Timestamp", "gl:Timer", "gl:Delay")
PER_ELEMENT = {"rx:SelectMany", "rx:CreateObservable", "rx:Concat", "rx:Switch"}
# The subjects a lab block declares for the workflow, and their kind: a multicast into them is not seen in the workflow's XML.
SPEAKER_MIXER = "OpenRigs.Audio:SpeakerMixer.bonsai"
SPEAKER_MIXER_SOUNDS = ("StimulusSounds", "RewardToneSounds", "PenaltySounds", "BackgroundNoise")
BLOCK_SUBJECTS = {
    "OpenRigs.Devices:Zapit.ConnectZapit.bonsai": {"ZapitCommands": "rx:BehaviorSubject"},
    SPEAKER_MIXER: {name: "rx:BehaviorSubject" for name in SPEAKER_MIXER_SOUNDS},
    "OpenRigs.Devices:RigHardware.bonsai": {"ReadyStages": "rx:ReplaySubject"},
}
ZAPIT_INCLUDE = "OpenRigs.Devices:Zapit.ConnectZapit.bonsai"
RIG_HARDWARE = "OpenRigs.Devices:RigHardware.bonsai"
# The stages RigHardware's Ready waits for (its ReadyWaitsFor), and on a workflow that includes SpeakerMixer.
READY_STAGES = ("HarpClockSet", "SpoutsHomed")
READY_STAGES_WITH_MIXER = READY_STAGES + ("MixerStarted",)
# The PulseController's subject that fires a pulse of the line's set width (the DO line, 1 to 3, as an int).
FIXED_WIDTH_PULSE = "TriggerPulse"
# The lab's Bonsai setup, one copy that each workflow's .bonsai\Setup.ps1 runs from the open-rigs it pins.
SETUP_BONSAI = Path(__file__).parents[1] / "scripts" / "setup-bonsai.ps1"
SETUP_BONSAI_RUN = "open_rigs\\scripts\\setup-bonsai.ps1"
MANUAL_REWARD_DROP_UL = 2.5  # RewardLeft and RewardRight each give this, before and during a run (15 Nov 2026)


class Workflow:
    """A workflow repository's src\\main.bonsai (or another workflow in src), as text and as XML."""

    def __init__(self, repo: Path, file: str = "main.bonsai"):
        self.repo = Path(repo)
        self.text = (self.repo / "src" / file).read_text(encoding="utf-8-sig")
        self.root = ET.fromstring(self.text.encode("utf-8"))

    def named(self, name: str) -> ET.Element:
        """The first node named `name`, at any depth."""
        return next(e for e in self.root.iter(W + "Expression") if e.findtext(W + "Name") == name)


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


def finite_async_event_senders(wf: Workflow) -> tuple[int, list[str]]:
    """(the multicasts into AsyncEvents, the finite operators upstream of any): a MulticastSubject passes on its source's
    completion, so a Take ahead of it completed the shared subject at trial 1 (9 Nov 2026). A sender that
    logs once takes its one value after the multicast."""
    senders, finite = 0, []
    for nodes, edges in _scopes(wf.root):
        for i, node in enumerate(nodes):
            if node.get(XSI) == "MulticastSubject" and node.findtext(W + "Name") == "AsyncEvents":
                senders += 1
                finite += [kind(nodes[a]) for a in upstream(edges, i) if kind(nodes[a]) in FINITE]
    return senders, finite


def subjects_closed_by_a_nested_multicast(wf: Workflow) -> list[str]:
    """Each MulticastSubject inside a per-element nested graph whose subject is not a BehaviorSubject declared in the
    workflow, by a lab block it includes, or inside that graph. Bonsai 2.9 passes a multicast source's completion to a
    PublishSubject or ReplaySubject, so the first trial's graph closes it for good (seen on a rig, 14 Nov 2026)."""
    declared: dict[str, str] = {}
    for path, subjects in BLOCK_SUBJECTS.items():
        if f'Path="{path}"' in wf.text:
            declared.update(subjects)

    def is_subject(n: ET.Element) -> bool:
        return (n.get(XSI) or "").startswith("rx:") and (n.get(XSI) or "").endswith("Subject")

    for n in wf.root.iter(W + "Expression"):
        if is_subject(n):
            declared[n.findtext(RX + "Name", "")] = n.get(XSI, "")
    found: list[str] = []

    def walk(workflow: ET.Element, nested: bool, local: set[str]) -> None:
        nodes = child(workflow, W + "Nodes")
        local = local | {n.findtext(RX + "Name", "") for n in nodes if is_subject(n)}
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


def mixer_boot_delay_problems(root: ET.Element) -> list[str]:
    """The mixer starts no sooner than 3 s after Start, or rig.yml's longer bootDelaySeconds: a fault while the sound card
    boots can blue-screen the PC (deliberate; never remove or shorten it). `root` is OpenRigs.Audio's SpeakerMixer.bonsai,
    the one place the lab starts a mixer (lib-open-rigs tests it there)."""
    group = next(e for e in root.iter(W + "Expression") if e.findtext(W + "Name") == "Mixer")
    nodes, edges = scope(group)
    delays = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "MixerBootDelay"]
    if len(delays) != 1:
        return ["the Mixer group has no MixerBootDelay"]
    delay = nodes[delays[0]]
    problems = []
    if kind(delay) != "rx:DelaySubscription" or child(delay, W + "Combinator").findtext(RX + "DueTime") != "PT3S":
        problems.append("MixerBootDelay is not a DelaySubscription of PT3S")
    expressions = [nodes[a].findtext(SCRIPT + "Expression") for a in upstream(edges, delays[0])]
    if "TimeSpan.FromSeconds(Math.Max(3.0, SpeakerArray.Device.BootDelaySeconds))" not in expressions:
        problems.append("MixerBootDelay's DueTime is not max(3 s, speakerArray.device.bootDelaySeconds)")
    if [a for a, b, label in edges if b == delays[0] and label == "Source1"] != [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "MixerDevice"]:
        problems.append("MixerBootDelay does not delay MixerDevice, the mixer's start")
    return problems


def mixer_replay_problems(root: ET.Element) -> list[str]:
    """The mixer's AppendBuffer subscribes to a sound only once the mixer starts, after its boot delay, so a sound it
    read from its BehaviorSubject would replay a buffer sent during the boot, late. StimulusSounds, RewardToneSounds and
    PenaltySounds each reach their AppendBuffer through a PublishSubject (Mixer<name>), which ignores a per-trial
    sender's completion; StimulusSounds and PenaltySounds through a CheckMixerStarted of that sound and Mixer, which
    stops the workflow on one sent during the boot, RewardToneSounds straight (a manual drop before Go may lose its
    tone). BackgroundNoise, looped, is read straight from its BehaviorSubject. `root` is OpenRigs.Audio's
    SpeakerMixer.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))

    def name(n: ET.Element) -> str:
        return n.findtext(W + "Name") or n.findtext(RX + "Name") or ""

    def inputs(i: int) -> list[tuple[str, str, str]]:
        return sorted((label, nodes[a].get(XSI) or "", name(nodes[a])) for a, b, label in edges if b == i)

    def read_by_append_buffer(subject: str) -> bool:
        return any(kind(nodes[b]) == "mix:AppendBuffer" and label == "Source1" and nodes[a].get(XSI) == "SubscribeSubject"
                   and name(nodes[a]) == subject for a, b, label in edges)

    def checked(i: int, sound: str) -> bool:
        node = nodes[i]
        return (kind(node) == "aud:CheckMixerStarted" and child(node, W + "Combinator").findtext(AUDIO + "Sound") == sound
                and inputs(i) == [("Source1", "SubscribeSubject", sound), ("Source2", "SubscribeSubject", "Mixer")])

    problems = []
    for sound in SPEAKER_MIXER_SOUNDS[:3]:
        relays = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:PublishSubject" and name(n) == f"Mixer{sound}"]
        feeds = [a for r in relays for a, b, _ in edges if b == r]
        if sound == "RewardToneSounds":
            fed = [(nodes[a].get(XSI), name(nodes[a])) for a in feeds] == [("SubscribeSubject", sound)]
        else:
            fed = len(feeds) == 1 and checked(feeds[0], sound)
        if not (len(relays) == 1 and fed and read_by_append_buffer(f"Mixer{sound}")):
            problems.append(f"{sound} reaches the mixer other than through the PublishSubject Mixer{sound}"
                            + ("" if sound == "RewardToneSounds" else f", fed by a CheckMixerStarted of {sound} and Mixer"))
    if not read_by_append_buffer("BackgroundNoise"):
        problems.append("BackgroundNoise is not read straight from its BehaviorSubject by the mixer")
    return problems


def speaker_mixer_problems(wf: Workflow) -> list[str]:
    """Every sound plays through OpenRigs.Audio's SpeakerMixer, included once, which loads the speakers and starts the
    mixer no sooner than 3 s after Start: the workflow starts no mixer, makes no source and loads no speakers itself."""
    problems = [] if wf.text.count(f'Path="{SPEAKER_MIXER}"') == 1 else ["the workflow does not include OpenRigs.Audio's SpeakerMixer once"]
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
    problems += [f"the workflow scales a sound itself ({op})" for op in ("dsp:ConvertScale", "dsp:Rescale") if f'"{op}"' in wf.text]
    if re.search(r"<aud:Gain>|Property Name=\"Gain\"", wf.text):
        problems.append("a sound's Gain is set in the workflow")
    return problems


def rig_hardware_problems(wf: Workflow) -> list[str]:
    """RigHardware reports Ready, starts the cameras and holds the startup watchdog: the workflow repeats none of it.
    On a workflow that includes SpeakerMixer, RigHardware's ReadyWaitsFor adds MixerStarted, so Ready (and Go) waits for
    the mixer's boot; on one without, it is left at HarpClockSet, SpoutsHomed, as nothing would send MixerStarted."""
    includes = [n for n in wf.root.iter(W + "Expression") if n.get("Path") == RIG_HARDWARE]
    if len(includes) != 1:
        return ["the workflow does not include RigHardware once"]
    problems = [f"the workflow has its own {what}" for what, found in (
        ("Ready report", re.search(r"<orcl:Phase>Ready</orcl:Phase>", wf.text)),
        ("StartupWatchdog", ":StartupWatchdog\"" in wf.text),
        ("StartCameras", "<Name>StartCameras</Name>" in wf.text)) if found]
    waits = includes[0].findtext(W + "ReadyWaitsFor")
    given = tuple(s.strip() for s in waits.split(",") if s.strip()) if waits is not None else READY_STAGES
    mixer = f'Path="{SPEAKER_MIXER}"' in wf.text
    wanted = READY_STAGES_WITH_MIXER if mixer else READY_STAGES
    if given != wanted:
        problems.append(f"RigHardware's ReadyWaitsFor is {', '.join(given)}, not {', '.join(wanted)}, on a workflow "
                        + ("that includes SpeakerMixer" if mixer else "without SpeakerMixer"))
    return problems


def _name(n: ET.Element) -> str:
    return n.findtext(W + "Name") or n.findtext(RX + "Name") or ""


def _value(n: ET.Element) -> str:
    return child(n, W + "Combinator").findtext(W + "Value") or ""


def rig_ready_problems(root: ET.Element) -> list[str]:
    """RigHardware reports Ready from its ReadyWatchdog alone, a StartupWatchdog that passes on once every stage its
    ReadyWaitsFor names has come, over its own HarpClockSet and SpoutsHomed merged with ReadyStages, the ReplaySubject a
    block that holds Ready multicasts its stage into (SpeakerMixer's MixerStarted). `root` is OpenRigs.Devices'
    RigHardware.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))

    def sources(i: int) -> list[int]:
        return [a for a, b, _ in edges if b == i]

    ready = [i for i, n in enumerate(nodes) if _name(n) == "Ready" and kind(n) == "orcl:ReportPhase"]
    watchdog = sources(ready[0]) if len(ready) == 1 else []
    if not (len(watchdog) == 1 and _name(nodes[watchdog[0]]) == "ReadyWatchdog"
            and kind(nodes[watchdog[0]]) == "core:StartupWatchdog"):
        return ["Ready is not fed by ReadyWatchdog alone"]
    problems = []
    expected = child(nodes[watchdog[0]], W + "Combinator").findtext("{clr-namespace:OpenRigs.Core;assembly=OpenRigs.Core}Expected")
    if tuple(s.strip() for s in (expected or "").split(",")) != READY_STAGES:
        problems.append(f"ReadyWatchdog's Expected is not {', '.join(READY_STAGES)} by default")
    mapped = [p.get("DisplayName") for a in sources(watchdog[0]) if nodes[a].get(XSI) == "ExternalizedMapping"
              for p in nodes[a].iter(W + "Property") if p.get("Name") == "Expected"]
    if mapped != ["ReadyWaitsFor"]:
        problems.append("ReadyWatchdog's Expected is not RigHardware's ReadyWaitsFor")
    merge = [a for a in sources(watchdog[0]) if kind(nodes[a]) == "rx:Merge"]
    stages = {(_name(nodes[a]) if nodes[a].get(XSI) == "SubscribeSubject" else _value(nodes[a]) if kind(nodes[a]) == "StringProperty" else "")
              for a in (sources(merge[0]) if len(merge) == 1 else [])}
    if stages != set(READY_STAGES) | {"ReadyStages"}:
        problems.append("ReadyWatchdog does not watch HarpClockSet, SpoutsHomed and ReadyStages merged")
    if not any(n.get(XSI) == "rx:ReplaySubject" and _name(n) == "ReadyStages" for n in nodes):
        problems.append("RigHardware does not declare the ReplaySubject ReadyStages")
    return problems


def mixer_ready_problems(root: ET.Element) -> list[str]:
    """SpeakerMixer multicasts MixerStarted into RigHardware's ReadyStages once the mixer has started (its Mixer
    subject's value), the stage Ready waits for on a workflow that plays sound. `root` is OpenRigs.Audio's
    SpeakerMixer.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    for a, b, _ in edges:
        for c in [c for b2, c, _ in edges if b2 == b]:
            first, middle, last = nodes[a], nodes[b], nodes[c]
            if (first.get(XSI) == "SubscribeSubject" and _name(first) == "Mixer"
                    and kind(middle) == "StringProperty" and _value(middle) == "MixerStarted"
                    and last.get(XSI) == "MulticastSubject" and _name(last) == "ReadyStages"):
                return []
    return ["SpeakerMixer does not multicast MixerStarted into ReadyStages once the mixer has started"]


def fixed_width_pulses_on_reward_valves(wf: Workflow, valve_lines: set[int]) -> list[str]:
    """Every fixed-width pulse the workflow may fire on a RewardValve line (`valve_lines`, rig.yml's, from
    LabRig.reward_valve_lines). A valve gives water only in pulses sized for it: a reward's by DeliverReward
    (LoadRewardLine, PlanReward), a calibration's by its own FirePulses, both on TriggerCalibratedPulse. Refused: a
    value multicast into the PulseController's TriggerPulse (a pulse of whatever width the line's register holds)
    unless an IntProperty gives it a line that is not a valve's, and a board message that pulses, sets or sizes a
    valve's line itself (OutputSet, OutputPulseEnable, PulseDO<n>)."""
    subjects = {FIXED_WIDTH_PULSE} | set(re.findall(r"<TriggerPulseSubjectName>(\w+)</TriggerPulseSubjectName>", wf.text))
    found = []
    for nodes, edges in _scopes(wf.root):
        for i, node in enumerate(nodes):
            if node.get(XSI) == "MulticastSubject" and _name(node) in subjects:
                feeds = [nodes[a] for a, b, _ in edges if b == i]
                if len(feeds) != 1 or kind(feeds[0]) != "IntProperty":
                    found.append(f"{_name(node)} is sent a line no IntProperty gives, so it may be a RewardValve's")
                elif int(_value(feeds[0])) in valve_lines:
                    found.append(f"{_name(node)} fires a fixed-width pulse on DO{_value(feeds[0])}, a RewardValve")
            payload = next((e for e in node if e.tag.endswith("}Payload")), None)
            if payload is None:
                continue
            ptype = payload.get(XSI) or ""
            written = " ".join(payload.itertext())
            for line in sorted(valve_lines):
                if ptype.endswith(f":CreatePulseDO{line}Payload") or (
                        re.search(r":Create(OutputSet|OutputPulseEnable)Payload$", ptype) and re.search(rf"\bDO{line}\b", written)):
                    found.append(f"{_name(node) or ptype.split(':')[-1]} writes DO{line}, a RewardValve, itself")
    return found

def zapit_include_problems(wf: Workflow) -> list[str]:
    """Zapit is lib-open-rigs' Zapit.ConnectZapit, given the task's conditions file in an optogenetics experiment, and
    nothing of its connect, check and refusal chain is built in the workflow."""
    nodes, edges = scope(wf.named("Zapit"))
    includes = [i for i, n in enumerate(nodes) if n.get("Path") == ZAPIT_INCLUDE]
    if len(includes) != 1:
        return ["the Zapit group does not include Zapit.ConnectZapit once"]
    path = [nodes[i] for i in upstream(edges, includes[0])]
    problems = []
    if not any(n.findtext(W + "Selector") == "TaskParameters.Optogenetics.ZapitConfigPath" for n in path):
        problems.append("ConnectZapit is not given TaskParameters.Optogenetics.ZapitConfigPath")
    if not any(n.get(XSI) == "rx:Condition" and "Optogenetics.UseZapit" in ET.tostring(n, encoding="unicode") for n in path):
        problems.append("ConnectZapit is not behind a Condition on Optogenetics.UseZapit")
    problems += [f"the workflow builds {local} itself" for local in ("zap:ZapitClient", "zap:CheckZapitConditionCount", "ConnectZapit</Name>")
                 if local in wf.text]
    problems += ["the workflow declares ZapitCommands, which ConnectZapit declares"
                 for n in wf.root.iter(W + "Expression") if n.get(XSI, "").startswith("rx:") and n.findtext(RX + "Name") == "ZapitCommands"]
    return problems


def manual_reward_problems(wf: Workflow) -> list[str]:
    """RewardLeft and RewardRight each give ManualRewardDropUl, 2.5 ul, at the side rig.yml names; no setting."""
    problems = []
    if "ManualRewardUl" in wf.text:
        problems.append("ManualRewardUl is still named")
    nodes, edges = scope(wf.named("Initialization"))
    subject = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:BehaviorSubject" and n.findtext(W + "Name") == "ManualRewardDropUl"]
    sources = [a for a, b, _ in edges if subject and b == subject[0]]
    if len(sources) != 1 or float(nodes[sources[0]].findtext(f".//{W}Value") or "nan") != MANUAL_REWARD_DROP_UL:
        problems.append(f"ManualRewardDropUl is not a BehaviorSubject of {MANUAL_REWARD_DROP_UL} in Initialization")
    users = [n.findtext(W + "Name") for n in wf.named("Commands").iter(W + "Expression") if n.get(XSI) == "SubscribeSubject"]
    if users.count("ManualRewardDropUl") != 2:
        problems.append("RewardLeft and RewardRight do not both give ManualRewardDropUl")
    return problems


def penalty_sound_problems(wf: Workflow) -> list[str]:
    """The penalty sound is OpenRigs.Audio's PenaltySoundBuffer, made once the task file's penalty.sound.levelDbfs is in
    (Zip with Speakers, InputMapping LevelDbfs from Item1), never at the default level from the speakers alone."""
    for nodes, edges in _scopes(wf.root):
        buffers = [i for i, n in enumerate(nodes) if kind(n) == "aud:PenaltySoundBuffer"]
        if not buffers:
            continue
        mapping = [a for a, b, _ in edges if b == buffers[0]]
        if len(mapping) != 1 or nodes[mapping[0]].get(XSI) != "InputMapping" or \
                [(p.get("Name"), p.get("Selector")) for p in nodes[mapping[0]].iter(W + "Property")] != [("LevelDbfs", "Item1")]:
            return ["PenaltySoundBuffer is not fed by an InputMapping of LevelDbfs from Item1"]
        zips = [a for a, b, _ in edges if b == mapping[0]]
        inputs = [nodes[a] for a in upstream(edges, zips[0])] if zips and kind(nodes[zips[0]]) == "rx:Zip" else []
        problems = []
        if not any("TaskParameters.Penalty.Sound.LevelDbfs" in (n.findtext(W + "Selector") or "") for n in inputs):
            problems.append("the penalty sound's level is not the task file's penalty.sound.levelDbfs")
        if not any(n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "Speakers" for n in inputs):
            problems.append("the penalty sound is not made from Speakers")
        return problems + (["the workflow reads its own penalty sound file"] if "Sound.Path" in wf.text else [])
    return ["no PenaltySoundBuffer"]

def readme_missing_blocks(wf: Workflow) -> list[str]:
    """Every OpenRigs operator and include the workflow uses that its README's Shared Blocks table does not name."""
    readme = (wf.repo / "README.md").read_text(encoding="utf-8")
    table = readme[readme.index("## Shared Blocks"):]
    table = table[: table.index("\n## ", 4)] if "\n## " in table[4:] else table
    prefixes = [p for p, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+);', wf.text) if ns.startswith("OpenRigs")]
    used = {name for p in prefixes for name in re.findall(r'xsi:type="' + p + r':(\w+)"', wf.text)}
    used |= {p.split(":")[1].replace(".bonsai", "").split(".")[-1] for p in re.findall(r'Path="(OpenRigs\.[^"]+)"', wf.text)}
    return sorted(b for b in used if f"`{b}`" not in table)


def lick_spout_stage_commands(wf: Workflow) -> set[str]:
    """The OnCommand names OpenRigs.Devices' HarpLickSpoutStage listens for, read from the package the workflow uses."""
    nupkg = next((wf.repo / "local_packages").glob("OpenRigs.Devices.*.nupkg"))
    with zipfile.ZipFile(nupkg) as z:
        dll = z.read(next(n for n in z.namelist() if n.endswith("lib/net472/OpenRigs.Devices.dll"))).decode("latin-1")
    start = dll.index("The two-driver lick spout stage")
    return set(re.findall(r'OnCommand">\s*<\w+:Name>(\w+)<', dll[start:dll.index("</WorkflowBuilder>", start)]))


def undeclared_commands(wf: Workflow) -> list[str]:
    """Every command the workflow or its lick spout stage handles that its ExperimentControl does not declare:
    ControlServer refuses an undeclared command, so it would never fire."""
    declared = set(re.findall(r"<Command>\s*<Name>(\w+)</Name>", wf.text))
    handled = set(re.findall(r"<orcl:Name>(\w+)</orcl:Name>", ET.tostring(wf.named("Commands"), encoding="unicode")))
    if "OpenRigs.Devices:RigHardware.bonsai" in wf.text:
        handled |= lick_spout_stage_commands(wf)
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
    """The RngSeed event is logged once, at TaskLogic's top level from trial 1's Go (TrialGo), and nowhere else."""
    nodes, edges = scope(wf.named("TaskLogic"))
    seeds = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "RngSeed"]
    everywhere = [n for n in wf.root.iter(W + "Expression") if n.findtext(W + "Name") == "RngSeed"]
    if len(seeds) != 1 or len(everywhere) != 1:
        return [f"RngSeed is {len(everywhere)} node(s), {len(seeds)} at TaskLogic's top level; it is one, there"]
    sources = [nodes[a] for a, b, _ in edges if b == seeds[0]]
    if len(sources) != 1 or sources[0].get(XSI) != "SubscribeSubject" or sources[0].findtext(W + "Name") != "TrialGo":
        return ["RngSeed is not fed by TrialGo alone"]
    return []


def camera_log_problems(wf: Workflow) -> list[str]:
    """The cameras are logged by OpenRigs.Logging's LogCameras inside ExperimentLogs, which PathPrefix starts."""
    logs = ET.tostring(wf.named("ExperimentLogs"), encoding="unicode")
    problems = [] if logs.count('Path="OpenRigs.Logging:LogCameras.bonsai"') == 1 else ["ExperimentLogs does not include LogCameras once"]
    return problems + (["the workflow logs a camera itself"] if "LogVideoFfmpeg.bonsai" in wf.text or "LogVideo.bonsai" in wf.text else [])
