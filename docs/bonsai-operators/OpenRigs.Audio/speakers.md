# Speakers and Sounds

`OpenRigs.Audio` plays sounds on a rig's speakers: it reads the speakers from `rig.yml`, starts the speaker mixer (Bonsai.Mixer) in one shared block, `SpeakerMixer`, and lays each sound out, at its level and equalised for each speaker with its filter, as the buffer the mixer plays. It also carries the lab's one penalty sound, so every task plays the same one. It works on any rig whose `rig.yml` has a `speakerArray`.

Add it to a workflow's `src\Extensions.csproj` and `.bonsai\Bonsai.config` like the other lab packages; in the editor its operators are in the Toolbox under **OpenRigs.Audio**.

---

## What it reads from rig.yml

```yaml
speakerArray:
  device:
    deviceName: Speakers (XMOS xCORE-200 MC (UAC2.0))
    sampleRate: 48000      # the mixer's rate; every filter and sound must be at it
    channelCount: 32       # rows of every buffer
    bootDelaySeconds: 3.0  # s before the mixer starts; SpeakerMixer never starts it sooner than 3 s
  speakers:
    left:
      position: {azimuth: -90.0, elevation: 15.0, radius: 1.0}
      channel: 0
      filter: calibration/speaker-left.json   # relative to rig.yml's folder
    right:
      position: {azimuth: 90.0, elevation: 15.0, radius: 1.0}
      channel: 1
      filter: calibration/speaker-right.json
  syncChannel: 31          # the sync copy's channel, needed by SyncCopy
  syncGain: 0.2            # the sync copy's level (0.2 when left out)
  penaltySoundChannel: 1     # the penalty sound's speaker: a speaker's channel
```

`syncGain` is the sync copy's linear amplitude, not a sound level. Each speaker needs a filter: a calibration artefact with `taps` (symmetric), `sampleRate` and `machineName` (see [Calibration](../../articles/calibration.md)). A speaker is never played unequalised, so `penaltySoundChannel` must be a speaker's channel. Each speaker has a channel of its own, no speaker is on `syncChannel`, and every channel, `syncChannel` included, is below `channelCount`.

---

## Levels

Every sound plays at a level, never a gain: `levelDbfs`, in dBFS RMS, the RMS of the sound over its whole length as the task gives it (one row, before equalisation) against full scale 1, so a full-scale square wave is 0 dBFS and a full-scale sine -3 dBFS (`SoundLevel.RmsDbfs`). The level sets the sound's gain (`SoundLevel.Gain`); equalisation then shapes it for each speaker. Every buffer's peak, on every channel, must stay under -1 dBFS after equalisation (`SoundLevel.CheckPeak`): a louder one stops the workflow, naming the sound, its peak and channel, and how far to lower its level. The lab's defaults (`SoundLevel`, and `open_rigs.core.task` for the task file) are stimuli -30, penalty sound -30, reward tone -36 and background noise -50; each task file states its own. They become dB SPL once each speaker is calibrated for loudness.

---

## The shared mixer

`OpenRigs.Audio:SpeakerMixer.bonsai`, included once by every workflow that plays sound (its own, or its rig list's: `DomeHardware` holds one), ahead of every node that subscribes `Speakers`:

- loads the speakers once at Start (`RigFile` -> `LoadSpeakers` -> `Speakers`, an `AsyncSubject`);
- starts the mixer on `rig.yml`'s `speakerArray.device` (from `RigConfigs`), never sooner than 3 s after Start, or `device.bootDelaySeconds` if longer: deliberate, as a fault while the sound card boots can blue-screen the PC, so never remove or shorten it;
- plays four sounds over each other, one mixer source each, from the `BehaviorSubject` of `Mat` it declares for it: `StimulusSounds` (each trial's stimulus), `RewardToneSounds`, `PenaltySounds` and `BackgroundNoise` (one buffer, looped, faded in over 2 s when it comes). Each buffer already carries its level, so the mixer plays them at gain 1. The mixer reads `StimulusSounds`, `RewardToneSounds` and `PenaltySounds` through a `PublishSubject` each (`Mixer<name>`), so a sound sent before the mixer has started is never played at its start: a stimulus or penalty sound sent then stops the workflow, naming it and the boot delay (`CheckMixerStarted`), as the trial log would record it as sent; a reward tone sent then is not played, as a manual drop before Go may be. Once the mixer has started it announces `MixerStarted` into `ReadyGate`'s `ReadyStages` (`AnnounceStage`); a workflow that includes `SpeakerMixer` adds `MixerStarted` to `ReadyGate`'s `ReadyWaitsFor` (`DomeHardware`'s already waits for it), so Ready, and with it Go, comes only once the mixer plays (see [Building Blocks](../../articles/building-blocks.md#ready)), and `CheckMixerStarted` is the safety net behind it. A sound sent while the one before it on its source is still playing is queued behind it (`AppendBuffer` appends to the source's playback queue): it starts only after the previous one has finished. `BackgroundNoise` plays when the mixer starts. A workflow multicasts only what it plays, from a per-trial graph if it likes (a `BehaviorSubject` is not closed by it).

It needs `ReadLabRig` (`RigFile`, `RigConfigs`), `ReadyGate` (`ReadyStages`) and Bonsai.Mixer in the workflow's environment.

---

## Operators

| Operator | Input | Output | What it does |
|---|---|---|---|
| `LoadSpeakers` | the path of this machine's `rig.yml` (ReadLabRig's `RigFile` subject) | `SpeakerSet` | Loads every speaker, its channel and its filter, once. Stops the workflow, naming every problem, on a speaker without a filter, or a filter missing, without taps, not symmetric, for another rate or measured on another machine (`MachineName`, empty for this one); and on a speaker or `syncChannel` outside the device's channels, two speakers on one channel, or a `penaltySoundChannel` with no speaker on it. `SpeakerMixer` runs it into `Speakers`. |
| `SpeakerBuffer` | `(SpeakerSet, Mat)` or `(SpeakerSet, IList<Mat>)` | `Mat` or `IList<Mat>` | The mixer's buffer for a sound of one row at `LevelDbfs`: on each of `Channels` (empty: every speaker in `rig.yml`), equalised for that speaker (`Equalise`, true for a stimulus, false for a background noise); every other channel silent. `SyncCopy` (a stimulus) adds the sound's sync copy: `syncGain` on `syncChannel` for the sound's whole length, so acquisition sees exactly when it plays; it stops the workflow on a `rig.yml` with no `syncChannel`. A list gives one buffer per sound, each at the level, such as a table of tones. Map `LevelDbfs` from the task file. |
| `PenaltySoundBuffer` | `SpeakerSet` | `Mat` | The lab's penalty sound (`penalty_tone.wav`, embedded in the package) on `rig.yml`'s `penaltySoundChannel` alone, equalised for that speaker, at `LevelDbfs`. Map it from the task's `penalty.sound.levelDbfs`, and make the buffer once the task file is read, not at each penalty. |
| `CheckMixerStarted` | `(sounds, the mixer)` | the sounds | Passes on each sound once the mixer has started, and stops the workflow, naming `Sound` and the mixer's boot delay, on a sound sent before. `SpeakerMixer` runs `StimulusSounds` and `PenaltySounds` through it. |
| `CheckWaveRate` | the mixer's sample rate | the rate | Stops the workflow if a WAV file the mixer plays (`FileName`, relative to `src`) is at another rate: nothing converts it, so it would play at the wrong pitch and speed. |

`SpeakerSet` (C#) has the speakers (`Speakers`, each with `Name`, `Channel`, `AzimuthDeg`, `ElevationDeg`), `SpeakerChannels`, `SampleRate`, `ChannelCount`, `SyncChannel`, `SyncGain`, `PenaltySoundChannel`, `Equalise(sound, channel)`, `Buffer(sound, channels, levelDbfs, equalise, syncCopy, what)` (at a level, peaks checked), `Layout(sound, channels, gain, equalise, syncCopy)` (a linear gain, for a sound equalised once at load whose gain comes from its unequalised self, peaks checked by the caller) and `AddSyncCopy(buffer)`, for a task's own C# such as a stimulus set equalised once at load. `Wave.Read(path, sampleRate)` reads a mono 16-bit PCM WAV as a float row, refusing another rate.

---

## In a workflow

```
IncludeWorkflow OpenRigs.Devices:DomeHardware.bonsai        (holds SpeakerMixer: Speakers, the mixer, its four sound subjects, MixerStarted)

SubscribeSubject TaskConfig -> TaskParameters.<sound>.LevelDbfs ─┐
SubscribeSubject Speakers ─┐                                       │
(a sound, one row)       ──┴─ Zip ────────────────────────────────┴─ Zip
  -> InputMapping (LevelDbfs from Item1, input Item2) -> SpeakerBuffer -> MulticastSubject StimulusSounds
```

The penalty sound waits for the task file's level the same way:

```
SubscribeSubject TaskConfig -> TaskParameters.Penalty.Sound.LevelDbfs ─┐
SubscribeSubject Speakers                                               ──┴─ Zip
  -> InputMapping (LevelDbfs from Item1, input Item2) -> PenaltySoundBuffer -> Take 1 -> AsyncSubject
```

A buffer has `rig.yml`'s `channelCount` rows, so a long sound is large (60 s on 32 channels at 48 kHz is about 370 MB as float32).