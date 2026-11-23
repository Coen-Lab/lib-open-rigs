"""The rules lib-open-rigs' own includes keep, checked by its tests on those files (no workflow calls them)."""

import xml.etree.ElementTree as ET

from open_rigs.core.task import BACKGROUND_LEVEL_DBFS
from open_rigs.rigs.workflow_lint import (
    AUDIO,
    RX,
    SCRIPT,
    SPEAKER_MIXER_SOUNDS,
    XSI,
    ZAPIT_CAMERA,
    ZAPIT_INCLUDE,
    ZAPIT_SETUP,
    W,
    _name,  # pyright: ignore[reportPrivateUsage]
    _named,  # pyright: ignore[reportPrivateUsage]
    child,
    kind,
    nodes_and_edges,
    scope,
    upstream,
)


def _value(n: ET.Element) -> str:
    return child(n, W + "Combinator").findtext(W + "Value") or ""


MANUAL_REWARD_DROP_UL = 2.5  # each valve's manual drop (RewardValve-Left.Reward) gives this, before and during a run


def mixer_boot_delay_problems(root: ET.Element) -> list[str]:
    """The mixer starts no sooner than 3 s after Start, or rig.yml's longer bootDelaySeconds: a fault while the sound card
    boots can blue-screen the PC (deliberate; never remove or shorten it). `root` is OpenRigs.Audio's SpeakerMixer.bonsai,
    the one place the lab starts a mixer (lib-open-rigs tests it there)."""
    group = _named(root, "Mixer", "SpeakerMixer.bonsai")
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

    def inputs(i: int) -> list[tuple[str, str, str]]:
        return sorted((label, nodes[a].get(XSI) or "", _name(nodes[a])) for a, b, label in edges if b == i)

    def appends(node: ET.Element, fed_by: str) -> bool:
        if kind(node) == "mix:AppendBuffer":
            return True
        if node.get(XSI) != "rx:SelectMany":  # StimulusSounds: each buffer appended to the StimulusSource of the moment
            return False
        inner, inner_edges = nodes_and_edges(child(node, W + "Workflow"))
        return any(kind(inner[b]) == "mix:AppendBuffer" and label == "Source1" and inner[a].get(XSI) == "WorkflowInput"
                   for a, b, label in inner_edges) and fed_by == "MixerStimulusSounds"

    def read_by_append_buffer(subject: str) -> bool:
        return any(appends(nodes[b], subject) and label == "Source1" and nodes[a].get(XSI) == "SubscribeSubject"
                   and _name(nodes[a]) == subject for a, b, label in edges)

    def checked(i: int, sound: str) -> bool:
        node = nodes[i]
        return (kind(node) == "aud:CheckMixerStarted" and child(node, W + "Combinator").findtext(AUDIO + "Sound") == sound
                and inputs(i) == [("Source1", "SubscribeSubject", sound), ("Source2", "SubscribeSubject", "Mixer")])

    problems = []
    for sound in SPEAKER_MIXER_SOUNDS[:3]:
        relays = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:PublishSubject" and _name(n) == f"Mixer{sound}"]
        feeds = [a for r in relays for a, b, _ in edges if b == r]
        if sound == "RewardToneSounds":
            fed = [(nodes[a].get(XSI), _name(nodes[a])) for a in feeds] == [("SubscribeSubject", sound)]
        else:
            fed = len(feeds) == 1 and checked(feeds[0], sound)
        if not (len(relays) == 1 and fed and read_by_append_buffer(f"Mixer{sound}")):
            problems.append(f"{sound} reaches the mixer other than through the PublishSubject Mixer{sound}"
                            + ("" if sound == "RewardToneSounds" else f", fed by a CheckMixerStarted of {sound} and Mixer"))
    if not read_by_append_buffer("BackgroundNoise"):
        problems.append("BackgroundNoise is not read straight from its BehaviorSubject by the mixer")
    return problems


def zapit_daq_for_task_problems(root: ET.Element) -> list[str]:
    """ConnectForTaskZapitDaq opens ZapitDaq, with every channel mapped from rig.yml's zapit block through Rigs.Zapit's full
    paths, ZapitCamera and ZapitSetup, all in one SelectMany behind a Condition on rig.yml's Zapit naming its device, and
    publishes Optogenetics from optogenetics.enabled and OptogeneticsLaserEnabled. `root` is OpenRigs.Devices' ConnectForTaskZapitDaq.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    problems = []
    groups = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:SelectMany" and _name(n) == "Zapit"]
    if len(groups) != 1:
        return ["ConnectForTaskZapitDaq has no one SelectMany named Zapit"]
    feed = [nodes[i] for i in upstream(edges, groups[0])]
    if not any(n.findtext(W + "Selector") == "Zapit" for n in feed) or not any(
            n.get(XSI) == "rx:Condition" and "it.Device != null" in ET.tostring(n, encoding="unicode") for n in feed):
        problems.append("ConnectForTaskZapitDaq's Zapit group is not fed rig.yml's Zapit behind a Condition on its device")
    inner = child(nodes[groups[0]], W + "Workflow")
    paths = {n.get("Path") for n in inner.iter(W + "Expression")}
    problems += [f"ConnectForTaskZapitDaq's Zapit group does not include {path}" for path in (ZAPIT_CAMERA, ZAPIT_SETUP) if path not in paths]
    daq = [n for n in inner.iter(W + "Expression") if kind(n).endswith(":ZapitDaq")]
    mapped = {p.get("Name"): p.get("Selector") for n in inner.iter(W + "Expression") if n.get(XSI) == "PropertyMapping"
              for p in n.iter(W + "Property")}
    if len(daq) != 1:
        problems.append("ConnectForTaskZapitDaq's Zapit group does not open one ZapitDaq")
    for channel in ("GalvoXChannel", "GalvoYChannel", "LaserChannel", "LedChannel", "PhotodiodeChannel"):
        if mapped.get(channel) != channel + "Path":
            problems.append(f"ZapitDaq's {channel} is not mapped from {channel}Path")
    for name in ("TriggerTerminal", "SampleRate", "MaxTrialSeconds", "EarlyStopLeadSeconds"):
        if mapped.get(name) != name:
            problems.append(f"ZapitDaq's {name} is not mapped from rig.yml's zapit block")
    if mapped.get("CeilingMw") != "LaserCeilingMw":
        problems.append("ZapitDaq's CeilingMw is not mapped from LaserCeilingMw")
    if mapped.get("LaserMaxVolts") != "LaserMaxControlVolts":
        problems.append("ZapitDaq's LaserMaxVolts is not mapped from LaserMaxControlVolts")
    if any(e.tag.endswith("}CeilingMw") and e.text != "0" for e in inner.iter()):
        problems.append("ZapitDaq's CeilingMw default is not 0 (a lost mapping must fail safe)")
    flags = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and _name(n) == "Optogenetics"]
    if len(flags) != 1 or "TaskParameters.Optogenetics.Enabled" not in {nodes[a].findtext(W + "Selector") for a in upstream(edges, flags[0])}:
        problems.append("ConnectForTaskZapitDaq does not publish Optogenetics from Optogenetics.Enabled")
    # The condition of the files only a beam makes (workflow_lint.ZAPIT_LASER_FILES): Optogenetics and ceilingMw above 0.
    laser = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and _name(n) == "OptogeneticsLaserEnabled"]
    feed = [nodes[a] for a in upstream(edges, laser[0])] if len(laser) == 1 else []
    if not ({"Optogenetics", "RigConfigs"} <= {n.findtext(W + "Name") for n in feed if n.get(XSI) == "SubscribeSubject"}
            and any("Item1 && Item2.Zapit != null && Item2.Zapit.LaserCeilingMw > 0" == n.findtext(SCRIPT + "Expression") for n in feed)):
        problems.append("ConnectForTaskZapitDaq does not publish OptogeneticsLaserEnabled as Optogenetics with rig.yml's LaserCeilingMw above 0")
    return problems


def zapit_for_task_problems(root: ET.Element) -> list[str]:
    """ConnectForTask gives ConnectZapit the task file's optogenetics.zapitConditionsPath behind a Condition on
    optogenetics.enabled. `root` is OpenRigs.Devices' ConnectForTask.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    includes = [i for i, n in enumerate(nodes) if n.get("Path") == ZAPIT_INCLUDE]
    if len(includes) != 1:
        return ["ConnectForTask does not include ConnectZapit once"]
    path = [nodes[i] for i in upstream(edges, includes[0])]
    problems = []
    if not any(n.findtext(W + "Selector") == "TaskParameters.Optogenetics.ZapitConditionsPath" for n in path):
        problems.append("ConnectZapit is not given TaskParameters.Optogenetics.ZapitConditionsPath")
    if not any(n.get(XSI) == "rx:Condition" and "Optogenetics.Enabled" in ET.tostring(n, encoding="unicode") for n in path):
        problems.append("ConnectZapit is not behind a Condition on Optogenetics.Enabled")
    flags = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and _name(n) == "Optogenetics"]
    if len(flags) != 1 or "TaskParameters.Optogenetics.Enabled" not in {nodes[a].findtext(W + "Selector") for a in upstream(edges, flags[0])}:
        problems.append("ConnectForTask does not publish Optogenetics from Optogenetics.Enabled")
    return problems


def manual_rewards_drop_problems(root: ET.Element) -> list[str]:
    """A valve's manual drop is MANUAL_REWARD_DROP_UL. `root` is OpenRigs.Devices' RewardValve.bonsai."""
    drops = [float(_value(n)) for n in root.iter(W + "Expression") if kind(n) == "DoubleProperty"]
    return [] if drops == [MANUAL_REWARD_DROP_UL] else [f"RewardValve's manual drop is {drops} ul, not {MANUAL_REWARD_DROP_UL}"]


def background_noise_include_problems(root: ET.Element) -> list[str]:
    """BackgroundNoise sends into BackgroundNoise only at Ready, rig.yml's noise read with the speakers (ReadBackgroundNoise), laid out
    unequalised at the lab's BACKGROUND_LEVEL_DBFS, reading no task file. `root` is OpenRigs.Audio's BackgroundNoise.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    sends = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and _name(n) == "BackgroundNoise"]
    if len(sends) != 1:
        return ["BackgroundNoise does not send into BackgroundNoise once"]
    up = [nodes[a] for a in upstream(edges, sends[0])]
    subjects = {_name(n) for n in up if n.get(XSI) == "SubscribeSubject"}
    problems = [f"the noise sent does not wait for {s}" for s in ("Ready", "Speakers") if s not in subjects]
    buffers = [n for n in up if kind(n) == "aud:SpeakerBuffer"]
    if len(buffers) != 1 or child(buffers[0], W + "Combinator").findtext(AUDIO + "Equalise") != "false":
        problems.append("the noise is not one SpeakerBuffer, unequalised")
    elif float(child(buffers[0], W + "Combinator").findtext(AUDIO + "LevelDbfs") or "nan") != BACKGROUND_LEVEL_DBFS:
        problems.append(f"the noise's level is not the lab's {BACKGROUND_LEVEL_DBFS:g} dBFS")
    if subjects & {"TaskConfig", "LaunchTaskConfig"}:
        problems.append("the noise reads the task file, which has no background setting")
    if not any(kind(n) == "aud:ReadBackgroundNoise" for n in up):
        problems.append("the noise is not rig.yml's backgroundNoise (ReadBackgroundNoise)")
    return problems


def play_penalty_sound_problems(root: ET.Element) -> list[str]:
    """PlayPenaltySound makes the PenaltySoundBuffer once the rules' max penalty level is in (TrialRules'
    MaxPenaltyLevelDbfs, Zip with Speakers, InputMapping LevelDbfs from Item1), never at the default level from the
    speakers alone, and sends it into PenaltySounds at each input's level (SoundAtLevel, its BufferLevelDbfs mapped from
    the same max level). `root` is OpenRigs.Audio's PlayPenaltySound.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    buffers = [i for i, n in enumerate(nodes) if kind(n) == "aud:PenaltySoundBuffer"]
    if len(buffers) != 1:
        return ["PlayPenaltySound does not make one PenaltySoundBuffer"]
    mapping = [a for a, b, _ in edges if b == buffers[0]]
    if len(mapping) != 1 or nodes[mapping[0]].get(XSI) != "InputMapping" or \
            [(p.get("Name"), p.get("Selector")) for p in nodes[mapping[0]].iter(W + "Property")] != [("LevelDbfs", "Item1")]:
        return ["PenaltySoundBuffer is not fed by an InputMapping of LevelDbfs from Item1"]
    zips = [a for a, b, _ in edges if b == mapping[0]]
    inputs = [nodes[a] for a in upstream(edges, zips[0])] if zips and kind(nodes[zips[0]]) == "rx:Zip" else []
    problems = []
    if not any(n.findtext(W + "Selector") == "MaxPenaltyLevelDbfs" for n in inputs) or \
            not any(n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "TrialRules" for n in inputs):
        problems.append("the penalty sound is not laid out at TrialRules' MaxPenaltyLevelDbfs")
    if not any(n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "Speakers" for n in inputs):
        problems.append("the penalty sound is not made from Speakers")
    sends = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and _name(n) == "PenaltySounds"]
    up = upstream(edges, sends[0]) if len(sends) == 1 else []
    if "WorkflowInput" not in {nodes[a].get(XSI) for a in up}:
        problems.append("PlayPenaltySound does not send the buffer into PenaltySounds on each input")
    scale = [a for a in up if kind(nodes[a]) == "aud:SoundAtLevel"]
    if len(scale) != 1 or not any(n.findtext(W + "Selector") == "MaxPenaltyLevelDbfs" for n in (nodes[a] for a in upstream(edges, scale[0]))):
        problems.append("PlayPenaltySound does not play each input at its level from the buffer's (SoundAtLevel, BufferLevelDbfs from MaxPenaltyLevelDbfs)")
    return problems
