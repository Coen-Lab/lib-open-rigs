# Speakers and Sounds

`OpenRigs.Audio` plays sounds on a rig's speakers: it reads the speakers from `rig.yml`, equalises a sound for each speaker with its filter, and lays it out as the buffer the speaker mixer (Bonsai.Mixer) plays. It also carries the lab's one error sound, so every task plays the same one. It works on any rig whose `rig.yml` has a `speakerArray`.

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
  syncChannel: 31          # optional: a task's sync copy
  errorSoundChannel: 2     # the error sound's speaker
```

Each speaker needs a filter: a calibration artefact with `taps` (symmetric), `sampleRate` and `machineName` (see [Calibration](../../articles/calibration.md)). A speaker is never played unequalised.

---

## Operators

| Operator | Input | Output | What it does |
|---|---|---|---|
| `LoadSpeakers` | the path of this machine's `rig.yml` (RigHardware's `RigFile` subject) | `SpeakerSet` | Loads every speaker, its channel and its filter, once. Stops the workflow, naming every problem, on a speaker without a filter, or a filter missing, without taps, not symmetric, for another rate or measured on another machine (`MachineName`, empty for this one). Keep its output in an `AsyncSubject`, such as `Speakers`. |
| `SpeakerBuffer` | `(SpeakerSet, Mat)` or `(SpeakerSet, IList<Mat>)` | `Mat` or `IList<Mat>` | The mixer's buffer for a sound of one row: on each of `Channels` (empty: every speaker in `rig.yml`), equalised for that speaker (`Equalise`, true for a stimulus, false for a background noise), times `Gain`; every other channel silent. A list gives one buffer per sound, such as a table of tones. |
| `ErrorSoundBuffer` | `SpeakerSet` | `Mat` | The lab's error sound (`error_tone.wav`, embedded in the package) on `rig.yml`'s `errorSoundChannel` alone, equalised for that speaker, at `ErrorSound.Level` (0.5) times `Gain`. Map `Gain` from the task's `punishment.sound.gain`, and make the buffer once at load, not at each error. |
| `CheckWaveRate` | the mixer's sample rate | the rate | Stops the workflow if a WAV file the mixer plays (`FileName`, relative to `src`) is at another rate: nothing converts it, so it would play at the wrong pitch and speed. |

`SpeakerSet` (C#) has the speakers (`Speakers`, each with `Name`, `Channel`, `AzimuthDeg`, `ElevationDeg`), `SpeakerChannels`, `SampleRate`, `ChannelCount`, `SyncChannel`, `ErrorSoundChannel`, `Equalise(sound, channel)` and `Buffer(sound, channels, gain, equalise)`, for a task's own C# such as a stimulus set equalised once at load. `Wave.Read(path, sampleRate)` reads a mono 16-bit PCM WAV as a float row, refusing another rate.

---

## In a workflow

Load the speakers once, then make each buffer from them:

```
SubscribeSubject RigFile -> LoadSpeakers -> Take 1 -> AsyncSubject Speakers

SubscribeSubject Speakers ─┐
(a sound, one row)       ──┴─ Zip -> SpeakerBuffer -> the mixer source's buffer subject
```

For the error sound, wait for the task file's gain before building it, or it is built at the default gain from the speakers alone:

```
SubscribeSubject TaskConfig -> TaskParameters.Punishment.Sound.Gain ─┐
SubscribeSubject Speakers                                          ──┴─ Zip
  -> InputMapping (Gain from Item1, input Item2) -> ErrorSoundBuffer -> Take 1 -> AsyncSubject
```

A buffer has `rig.yml`'s `channelCount` rows, so a long sound is large (60 s on 32 channels at 48 kHz is about 370 MB as float32).
