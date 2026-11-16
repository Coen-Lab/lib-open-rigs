"""The rules lib-open-rigs' own includes keep, checked by its tests on those files (no workflow calls them)."""

import xml.etree.ElementTree as ET

from open_rigs.rigs.workflow_lint import (
    AUDIO,
    RX,
    SCRIPT,
    SPEAKER_MIXER_SOUNDS,
    W,
    XSI,
    ZAPIT_INCLUDE,
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

    def read_by_append_buffer(subject: str) -> bool:
        return any(kind(nodes[b]) == "mix:AppendBuffer" and label == "Source1" and nodes[a].get(XSI) == "SubscribeSubject"
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


def zapit_for_task_problems(root: ET.Element) -> list[str]:
    """Zapit.ConnectForTask gives ConnectZapit the task file's optogenetics.zapitConfigPath behind a Condition on
    optogenetics.useZapit. `root` is OpenRigs.Devices' Zapit/ConnectForTask.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    includes = [i for i, n in enumerate(nodes) if n.get("Path") == ZAPIT_INCLUDE]
    if len(includes) != 1:
        return ["ConnectForTask does not include Zapit.ConnectZapit once"]
    path = [nodes[i] for i in upstream(edges, includes[0])]
    problems = []
    if not any(n.findtext(W + "Selector") == "TaskParameters.Optogenetics.ZapitConfigPath" for n in path):
        problems.append("ConnectZapit is not given TaskParameters.Optogenetics.ZapitConfigPath")
    if not any(n.get(XSI) == "rx:Condition" and "Optogenetics.UseZapit" in ET.tostring(n, encoding="unicode") for n in path):
        problems.append("ConnectZapit is not behind a Condition on Optogenetics.UseZapit")
    flags = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and _name(n) == "Optogenetics"]
    if len(flags) != 1 or "TaskParameters.Optogenetics.UseZapit" not in {nodes[a].findtext(W + "Selector") for a in upstream(edges, flags[0])}:
        problems.append("ConnectForTask does not publish Optogenetics from Optogenetics.UseZapit")
    return problems


def manual_rewards_drop_problems(root: ET.Element) -> list[str]:
    """A valve's manual drop is MANUAL_REWARD_DROP_UL. `root` is OpenRigs.Devices' BehaviorBoard/RewardValve.bonsai."""
    drops = [float(_value(n)) for n in root.iter(W + "Expression") if kind(n) == "DoubleProperty"]
    return [] if drops == [MANUAL_REWARD_DROP_UL] else [f"RewardValve's manual drop is {drops} ul, not {MANUAL_REWARD_DROP_UL}"]


def background_noise_include_problems(root: ET.Element) -> list[str]:
    """BackgroundNoise sends into BackgroundNoise only at Ready, rig.yml's noise read with the speakers (ReadBackgroundNoise), laid out
    unequalised at LaunchTaskConfig's backgroundNoise.levelDbfs; and stops at Go on a TaskConfig whose backgroundNoise
    differs from LaunchTaskConfig's. `root` is OpenRigs.Audio's BackgroundNoise.bonsai."""
    nodes, edges = nodes_and_edges(child(root, W + "Workflow"))
    sends = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and _name(n) == "BackgroundNoise"]
    if len(sends) != 1:
        return ["BackgroundNoise does not send into BackgroundNoise once"]
    up = [nodes[a] for a in upstream(edges, sends[0])]
    subjects = {_name(n) for n in up if n.get(XSI) == "SubscribeSubject"}
    problems = [f"the noise sent does not wait for {s}" for s in ("Ready", "LaunchTaskConfig", "Speakers") if s not in subjects]
    buffers = [n for n in up if kind(n) == "aud:SpeakerBuffer"]
    if len(buffers) != 1 or child(buffers[0], W + "Combinator").findtext(AUDIO + "Equalise") != "false":
        problems.append("the noise is not one SpeakerBuffer, unequalised")
    if "TaskParameters.BackgroundNoise.LevelDbfs" not in {n.findtext(W + "Selector") for n in up}:
        problems.append("the noise's level is not the launched task file's backgroundNoise.levelDbfs")
    if not any(kind(n) == "aud:ReadBackgroundNoise" for n in up):
        problems.append("the noise is not rig.yml's backgroundNoise (ReadBackgroundNoise)")
    stops = [i for i, n in enumerate(nodes) if kind(n) == "core:ThrowError"]
    if not any({"TaskConfig", "LaunchTaskConfig"} <= {_name(nodes[a]) for a in upstream(edges, s)} for s in stops):
        problems.append("Go does not stop on a backgroundNoise changed after launch")
    return problems


def play_penalty_sound_problems(root: ET.Element) -> list[str]:
    """PlayPenaltySound makes the PenaltySoundBuffer once the task file's penalty.sound.levelDbfs is in (Zip with
    Speakers, InputMapping LevelDbfs from Item1), never at the default level from the speakers alone, and sends it into
    PenaltySounds on each input. `root` is OpenRigs.Audio's PlayPenaltySound.bonsai."""
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
    if not any("TaskParameters.Penalty.Sound.LevelDbfs" in (n.findtext(W + "Selector") or "") for n in inputs):
        problems.append("the penalty sound's level is not the task file's penalty.sound.levelDbfs")
    if not any(n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "Speakers" for n in inputs):
        problems.append("the penalty sound is not made from Speakers")
    sends = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and _name(n) == "PenaltySounds"]
    if len(sends) != 1 or "WorkflowInput" not in {nodes[a].get(XSI) for a in upstream(edges, sends[0])}:
        problems.append("PlayPenaltySound does not send the buffer into PenaltySounds on each input")
    return problems
