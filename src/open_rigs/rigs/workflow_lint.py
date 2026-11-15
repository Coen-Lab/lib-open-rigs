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
FINITE = ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")
WALL_CLOCK = ("rx:Timer", "rx:Delay", "rx:DelaySubscription", "rx:Throttle", "rx:Timeout", "rx:Interval", "rx:TimeInterval",
              "rx:Timestamp", "gl:Timer", "gl:Delay")
PER_ELEMENT = {"rx:SelectMany", "rx:CreateObservable", "rx:Concat", "rx:Switch"}
# The subjects a lab block declares for the workflow, and their kind: a multicast into them is not seen in the workflow's XML.
BLOCK_SUBJECTS = {"OpenRigs.Devices:Zapit.ConnectZapit.bonsai": {"ZapitCommands": "rx:BehaviorSubject"}}
ZAPIT_INCLUDE = "OpenRigs.Devices:Zapit.ConnectZapit.bonsai"
MANUAL_REWARD_DROP_UL = 2.5  # Pip, 15 Nov 2026: RewardLeft and RewardRight each give this, before and during a run


class Workflow:
    """A workflow repository's src\\main.bonsai, as text and as XML."""

    def __init__(self, repo: Path):
        self.repo = Path(repo)
        self.text = (self.repo / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
        self.root = ET.fromstring(self.text.encode("utf-8"))

    def named(self, name: str) -> ET.Element:
        """The first node named `name`, at any depth."""
        return next(e for e in self.root.iter(W + "Expression") if e.findtext(W + "Name") == name)


def kind(node: ET.Element) -> str:
    """A node's type: its combinator's for a Combinator node."""
    combinator = node.find(W + "Combinator")
    return (combinator.get(XSI) if combinator is not None else node.get(XSI)) or ""


def scope(node: ET.Element) -> tuple[list[ET.Element], list[tuple[int, int, str]]]:
    """A nested workflow's nodes and edges (from, to, label)."""
    workflow = node.find(W + "Workflow")
    nodes = list(workflow.find(W + "Nodes"))
    edges = [(int(e.get("From")), int(e.get("To")), e.get("Label")) for e in workflow.find(W + "Edges")]
    return nodes, edges


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
        nodes = list(workflow.find(W + "Nodes"))
        edges = [(int(e.get("From")), int(e.get("To")), e.get("Label")) for e in workflow.find(W + "Edges")]
        yield nodes, edges


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
    completion, so a Take ahead of it completed the shared subject at trial 1 (Tom Childs, 9 Nov 2026). A sender that
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
    PublishSubject or ReplaySubject, so the first trial's graph closes it for good (BLUE-OPTO-2, 14 Nov 2026)."""
    declared: dict[str, str] = {}
    for path, subjects in BLOCK_SUBJECTS.items():
        if f'Path="{path}"' in wf.text:
            declared.update(subjects)

    def is_subject(n: ET.Element) -> bool:
        return (n.get(XSI) or "").startswith("rx:") and (n.get(XSI) or "").endswith("Subject")

    for n in wf.root.iter(W + "Expression"):
        if is_subject(n):
            declared[n.findtext(RX + "Name")] = n.get(XSI)
    found: list[str] = []

    def walk(workflow: ET.Element, nested: bool, local: set[str]) -> None:
        local = local | {n.findtext(RX + "Name") for n in workflow.find(W + "Nodes") if is_subject(n)}
        for n in workflow.find(W + "Nodes"):
            if n.get(XSI) == "MulticastSubject" and nested:
                name = n.findtext(W + "Name")
                if name not in local and declared.get(name) != "rx:BehaviorSubject":
                    found.append(name)
            inner = n.find(W + "Workflow")
            if inner is not None:
                per = n.get(XSI) in PER_ELEMENT
                walk(inner, nested or per, set() if per else local)

    walk(wf.root.find(W + "Workflow"), False, set())
    return found


def startup_watchdog_readers(wf: Workflow) -> int:
    """How many nodes read a StartupWatchdog's output: it completes once every stage has come and never emits, so a
    node fed by it never sees a value (GREEN-DOME-2, 14 Nov 2026: Ready never came)."""
    return sum(1 for nodes, edges in _scopes(wf.root) for i, n in enumerate(nodes)
               if kind(n).endswith(":StartupWatchdog") for a, _, _ in edges if a == i)


def mixer_boot_delay_problems(wf: Workflow) -> list[str]:
    """The mixer starts no sooner than 3 s after Start, or rig.yml's longer bootDelay: a fault while the sound card
    boots can blue-screen the PC (deliberate; never remove or shorten it)."""
    nodes, edges = scope(wf.named("Mixer"))
    delays = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "MixerBootDelay"]
    if len(delays) != 1:
        return ["the Mixer group has no MixerBootDelay"]
    delay = nodes[delays[0]]
    problems = []
    if kind(delay) != "rx:DelaySubscription" or delay.find(W + "Combinator").findtext(RX + "DueTime") != "PT3S":
        problems.append("MixerBootDelay is not a DelaySubscription of PT3S")
    expressions = [nodes[a].findtext(SCRIPT + "Expression") for a in upstream(edges, delays[0])]
    if "TimeSpan.FromSeconds(Math.Max(3.0, SpeakerArray.Device.BootDelay))" not in expressions:
        problems.append("MixerBootDelay's DueTime is not max(3 s, speakerArray.device.bootDelay)")
    return problems


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
    if len(sources) != 1 or float(next(nodes[sources[0]].iter(W + "Value")).text) != MANUAL_REWARD_DROP_UL:
        problems.append(f"ManualRewardDropUl is not a BehaviorSubject of {MANUAL_REWARD_DROP_UL} in Initialization")
    users = [n.findtext(W + "Name") for n in wf.named("Commands").iter(W + "Expression") if n.get(XSI) == "SubscribeSubject"]
    if users.count("ManualRewardDropUl") != 2:
        problems.append("RewardLeft and RewardRight do not both give ManualRewardDropUl")
    return problems


def penalty_sound_problems(wf: Workflow) -> list[str]:
    """The penalty sound is OpenRigs.Audio's PenaltySoundBuffer, made once the task file's penalty.sound.gain is in
    (Zip with Speakers, InputMapping Gain from Item1), never at the default gain from the speakers alone."""
    for nodes, edges in _scopes(wf.root):
        buffers = [i for i, n in enumerate(nodes) if kind(n) == "aud:PenaltySoundBuffer"]
        if not buffers:
            continue
        mapping = [a for a, b, _ in edges if b == buffers[0]]
        if len(mapping) != 1 or nodes[mapping[0]].get(XSI) != "InputMapping" or \
                [(p.get("Name"), p.get("Selector")) for p in nodes[mapping[0]].iter(W + "Property")] != [("Gain", "Item1")]:
            return ["PenaltySoundBuffer is not fed by an InputMapping of Gain from Item1"]
        zips = [a for a, b, _ in edges if b == mapping[0]]
        inputs = [nodes[a] for a in upstream(edges, zips[0])] if zips and kind(nodes[zips[0]]) == "rx:Zip" else []
        problems = []
        if not any("TaskParameters.Penalty.Sound.Gain" in (n.findtext(W + "Selector") or "") for n in inputs):
            problems.append("the penalty sound's gain is not the task file's penalty.sound.gain")
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
    """A tracked config\\main.layout, which .bonsai\\Setup.ps1 puts in place of a stale per-machine one."""
    problems = []
    layout = wf.repo / "config" / "main.layout"
    if not layout.is_file() or layout.read_text(encoding="utf-8").count("<VisualizerLayout") != 1:
        problems.append("config\\main.layout is not one VisualizerLayout")
    if "..\\config\\main.layout" not in (wf.repo / ".bonsai" / "Setup.ps1").read_text(encoding="utf-8-sig"):
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
