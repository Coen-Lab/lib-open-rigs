# Log Video (FFmpeg)

`LogVideoFfmpeg` writes a camera stream to disk as an H.264 video in a Matroska (`.mkv`) file and a CSV with one row per frame, through an `ffmpeg.exe` child process. It takes what [LogVideo](log-video.md) takes, so a workflow swaps one for the other by changing the include's path. The encoding happens in `ffmpeg.exe`, on the GPU where the rig has one, so the camera never waits for it, and the file is closed before the workflow lets the process end.

---

## Bonsai workflow

The workflow is `LogVideo`'s with the writer swapped. It groups incoming frames by the hour (`GroupByTime` with `ChunkSize = 1`), and for each hour [FfmpegVideoWriter](xref:OpenRigs.Video.FfmpegVideoWriter) writes `<LogName>\<LogName>_<DateTime>.mkv` and `.csv` under the `PathPrefix` subject published by [LogController](log-controller.md). File names come from `FormatFileName`, as for every other log.

To use it in place of `LogVideo`, change the include's path from `OpenRigs.Logging:LogVideo.bonsai` to `OpenRigs.Logging:LogVideoFfmpeg.bonsai` and keep its `LogName` and `FrameRate`. The input is the same: `Timestamped<VideoDataFrame>` from [Triggered Spinnaker](../OpenRigs.Devices/triggered-spinnaker.md). The workflow needs the OpenRigs.Video package, which a workflow with a Spinnaker camera already has.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `LogName` | - | Stem added after the path prefix (e.g. `Camera-Pupil`) |
| `FrameRate` | `50` | Frame rate written into the video, the camera trigger's; each frame's own time is its `harpTime` |
| `ConfigRoot` | the rig configuration root | Folder holding `<MACHINE-NAME>\rig.yml`, whose `videoEncoder` is read at Start |

### Metadata columns

The CSV has one row per frame in the video, in the video's order, written once the frame is in ffmpeg's input. A frame the writer dropped has no row, so row `n` is always frame `n` of the video. The CSV is flushed to disk once a second, so a Bonsai that is killed (a crash, or a rig agent killing it 30 s after a Stop it did not finish) can leave up to a second of frames at the end of the video with no row; a normal Stop loses none.

| Column | Description |
|--------|-------------|
| `harpTime` | Harp time of the frame's trigger, in seconds |
| `frameId` | The camera's frame counter (Spinnaker chunk `FrameID`) |
| `cameraClockNs` | The camera's own clock at the exposure, in nanoseconds (chunk `Timestamp`) |
| `exposureUs` | The exposure the camera reports for this frame, in microseconds; NaN where unknown |
| `gainDb` | The gain the camera reports for this frame, in dB; NaN where unknown |

---

## The encoder: rig.yml

Each machine names its encoder in `rig.yml`, one setting for every camera on it:

```yaml
videoEncoder:
  codec: h264_nvenc     # h264_nvenc (NVIDIA), h264_qsv (Intel Quick Sync) or libx264 (software)
  ffmpegPath: D:\Tools\ffmpeg\bin\ffmpeg.exe   # optional
```

Without `videoEncoder`, or without a `rig.yml`, the writer uses `libx264` and the first `ffmpeg.exe` on PATH. `libx264` runs anywhere but takes about three cores for three 1440 x 1080 cameras at 50 Hz, so a rig with a GPU or Quick Sync should name its encoder. `ffmpegPath` is for a machine where the account that runs Bonsai does not have FFmpeg on its PATH; `winget install Gyan.FFmpeg` puts it on the installing user's PATH. If `ffmpeg.exe` is not found, or `codec` or another key is not one the writer knows, the workflow stops at Start with an error naming it. The workflow's rig model must know the key too (`open_rigs.rigs.dome.DomeRig` and `open_rigs.rigs.behaviour_box.BehaviourBoxRig` do from this version), so update the workflow before adding `videoEncoder` to a rig's `rig.yml`.

Every codec writes H.264 with no B-frames and a keyframe every two seconds, at a constant quality (`-cq 20` on NVENC, `-global_quality 20` on Quick Sync, `-crf 20` on libx264), so a noisy camera costs bytes rather than frames.

---

## Stop, dropped frames and failures

Frames wait for ffmpeg in a queue of 250 (5 s at 50 Hz, about 390 MB of 1440 x 1080 Mono8 frames). The camera's thread only adds to it and never waits: a frame that finds the queue full is dropped and counted. A warning goes to stderr when 50 frames are waiting, and again when ffmpeg has caught up.

When the frames end, the writer closes ffmpeg's input and waits for it to finish the file, at most 20 s, then kills it. On a Stop the workflow does not wait for this: the writer finishes on its own thread, and that thread keeps the Bonsai process open until the file is closed, so headless Bonsai cannot exit with frames unwritten. The 20 s limit keeps the process from outliving a rig agent that kills Bonsai 30 s after Stop.

A recording that went wrong fails loudly. If ffmpeg exits while recording, the next frame stops the workflow with ffmpeg's exit code and its last error lines. If frames were dropped, ffmpeg exited with an error, or it had to be killed, a stream that completed ends with an error saying so; at a Stop, when nothing downstream can take an error, the message goes to stderr and the trace and into `<LogName>_<DateTime>.error.txt` beside the video, which the post-run check reports.

---

## Why Matroska

A Matroska file needs nothing written at its end to be read: ffmpeg writes the header first and then one cluster of frames at a time, a cluster at most every second. If ffmpeg is killed, every whole cluster stays readable, and readers stop at the cut. If Bonsai is killed instead, ffmpeg sees its input close and finishes the file itself. In a test that killed ffmpeg after 150 frames at 50 Hz, the file decoded to 130 frames. A fragmented MP4 is readable after a kill too, but only up to its last whole fragment, which ends at a keyframe, and some readers count its frames wrongly.

---

## Checking a recording

`check-videos <experiment folder>` (and `summarise-experiment --videos` for the dome tasks) counts each video's frames from the file, decoding every frame of an `.mkv` with `ffprobe -count_frames`, or walking every frame chunk of an `.avi` from `LogVideo`, and compares them with the CSV beside it. It also reports `frameId` steps other than 1 and any `.error.txt`. Each mismatch is a problem, and the command exits 1, with one exception: when the experiment's `manifest.json` says the run was killed (`run.final` false, because the end-of-run write never came, or `run.outcome` `running` or `killed`), a camera's last video holding more frames than its CSV, by at most one second of frames plus one (`ceil(fps) + 1`, 51 at 50 Hz, the frame rate read from the CSV's `harpTime` spacing), is printed as a `NOTE` ("killed run: N frames at the end have no CSV row") and does not fail the check. A video short of its CSV, more extra frames than that, extra frames in an earlier video, or extra frames in a run that was not killed (or a folder with no manifest) stay problems. Decoding a noisy 1440 x 1080 video runs at about 1,000 frames a second on a 20-thread machine, so a 2.5-hour session takes several minutes a camera.
