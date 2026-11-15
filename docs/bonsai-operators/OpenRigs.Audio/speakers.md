# Speakers and Sounds

`OpenRigs.Audio` plays sounds on a rig's speakers: it reads the speakers from `rig.yml`, equalises a sound for each speaker with its filter, and lays it out as the buffer the speaker mixer (Bonsai.Mixer) plays. It also carries the lab's one penalty sound, so every task plays the same one. It works on any rig whose `rig.yml` has a `speakerArray`.

Add it to a workflow's `src\Extensions.csproj` and `.bonsai\Bonsai.config` like the other lab packages; in the editor its operators are in the Toolbox under **OpenRigs.Audio**.

---

## What it reads from rig.yml

```yaml
speakerArray:
  device:
    sampleRate: 48000      # the mixer's rate; every filter and sound must be at it
    channelCount: 32       # rows of every buffer
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

Each speaker needs a filter: a calibration artefact with `taps` (symmetric), `sampleRate` and `machineName` (see [Calibration](../../articles/calibration.md)). A speaker is never played unequalised, so `penaltySoundChannel` must be a speaker's channel. Each speaker has a channel of its own, and every channel, `syncChannel` included, is below `channelCount`.

---

## Operators

| Operator | Input | Output | What it does |
|---|---|---|---|
| `LoadSpeakers` | the path of this machine's `rig.yml` (RigHardware's `RigFile` subject) | `SpeakerSet` | Loads every speaker, its channel and its filter, once. Stops the workflow, naming every problem, on a speaker without a filter, or a filter missing, without taps, not symmetric, for another rate or measured on another machine (`MachineName`, empty for this one); and on a speaker or `syncChannel` outside the device's channels, two speakers on one channel, or an `penaltySoundChannel` with no speaker on it. Keep its output in an `AsyncSubject`, such as `Speakers`. |
| `SpeakerBuffer` | `(SpeakerSet, Mat)` or `(SpeakerSet, IList<Mat>)` | `Mat` or `IList<Mat>` | The mixer's buffer for a sound of one row: on each of `Channels` (empty: every speaker in `rig.yml`), equalised for that speaker (`Equalise`, true for a stimulus, false for a background noise), times `Gain`; every other channel silent. `SyncCopy` (a stimulus) adds the sound's sync copy: `syncGain` on `syncChannel` for the sound's whole length, so acquisition sees exactly when it plays; it stops the workflow on a `rig.yml` with no `syncChannel`. A list gives one buffer per sound, such as a table of tones. |
| `PenaltySoundBuffer` | `SpeakerSet` | `Mat` | The lab's penalty sound (`penalty_tone.wav`, embedded in the package) on `rig.yml`'s `penaltySoundChannel` alone, equalised for that speaker, at `PenaltySound.Level` (0.5) times `Gain`. Map `Gain` from the task's `penalty.sound.gain`, and make the buffer once at load, not at each error. |
| `CheckWaveRate` | the mixer's sample rate | the rate | Stops the workflow if a WAV file the mixer plays (`FileName`, relative to `src`) is at another rate: nothing converts it, so it would play at the wrong pitch and speed. |

`SpeakerSet` (C#) has the speakers (`Speakers`, each with `Name`, `Channel`, `AzimuthDeg`, `ElevationDeg`), `SpeakerChannels`, `SampleRate`, `ChannelCount`, `SyncChannel`, `SyncGain`, `PenaltySoundChannel`, `Equalise(sound, channel)`, `Buffer(sound, channels, gain, equalise, syncCopy)` and `AddSyncCopy(buffer)`, for a task's own C# such as a stimulus set equalised once at load. `Wave.Read(path, sampleRate)` reads a mono 16-bit PCM WAV as a float row, refusing another rate.

---

## In a workflow

Load the speakers once, then make each buffer from them:

```
SubscribeSubject RigFile -> LoadSpeakers -> Take 1 -> AsyncSubject Speakers

SubscribeSubject Speakers ─┐
(a sound, one row)       ──┴─ Zip -> SpeakerBuffer -> the mixer source's buffer subject
```

For the penalty sound, wait for the task file's gain before building it, or it is built at the default gain from the speakers alone:

```
SubscribeSubject TaskConfig -> TaskParameters.Penalty.Sound.Gain ─┐
SubscribeSubject Speakers                                          ──┴─ Zip
  -> InputMapping (Gain from Item1, input Item2) -> PenaltySoundBuffer -> Take 1 -> AsyncSubject
```

A buffer has `rig.yml`'s `channelCount` rows, so a long sound is large (60 s on 32 channels at 48 kHz is about 370 MB as float32).
