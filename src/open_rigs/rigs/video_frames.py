"""Whether each camera video holds every frame its CSV lists: one experiment's videos checked from their files.

A camera logs <Device>/<Device>_<DateTime>.mkv (OpenRigs.Logging's LogVideoFfmpeg) or .avi (LogVideo), each with a .csv
beside it holding one row per frame. A video can hold fewer frames than its CSV lists when the writer did not finish
(LogVideo's last AVI at a Stop that came with frames still queued), and a reader that trusts the file's index sees fewer
still, so the frames are counted from the file itself:

- an .mkv (or any other container) by decoding every frame, `ffprobe -count_frames` with every core
  (about 1,000 frames a second for a noisy 1440 x 1080 camera on a 20-thread machine);
- an .avi by walking its chunks (every 00dc or 00db chunk in every movi list, the unfinished RIFF and movi lists of a
  killed writer included), without decoding, since its index is what an unfinished AVI gets wrong.

Each video is checked for: a CSV beside it; as many frames as CSV rows; no .error.txt beside it (FfmpegVideoWriter
leaves one saying what went wrong: frames dropped, ffmpeg failed or killed). Each camera's CSVs, in order, are checked
for frameId steps other than 1 (frames the camera or the writer lost); a CSV without a frame id column is a note. `check-videos <experiment folder> ...` prints
each video, each note and each problem, exiting 1 when there is a problem.

A killed run is the one exception to "as many frames as CSV rows". FfmpegVideoWriter flushes each CSV row just after
its frame goes into ffmpeg's stdin, and ffmpeg finishes the video after Bonsai has gone, so a Bonsai killed (a crash, or
the rig agent's kill after a Stop it did not answer) can leave a camera's last video one frame ahead of its CSV: the
frame in hand at the kill. Frames still queued at the kill reach neither the video nor the CSV. When the experiment's
manifest.json says the run was killed (run.final false: its end-of-run write never came; or outcome running, or
rig-sync's killed), a camera's last video holding at most KILL_TOLERANCE_FRAMES more frames than its CSV is a note, not
a problem; so is the video before it, which a kill during an hour split's handover (up to 20 s) leaves the same way.
Any other mismatch is a problem: a video short of its CSV, more frames than that, extra frames in an older video, or
extra frames in a run that was not killed. A folder without a readable manifest is taken as not killed.
"""

from __future__ import annotations

import argparse
import csv
import json
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional

VIDEO_SUFFIXES = (".mkv", ".avi", ".mp4")
# The frame counter's column: FfmpegVideoWriter's, LogVideo's (Bonsai member paths), then old behaviour-box CSVs'.
FRAME_ID_COLUMNS = ("frameId", "ChunkData.FrameID", "Value.ChunkData.FrameID", "FrameID")
# Each CSV row is flushed just after its frame reaches ffmpeg, so a kill can leave one frame in the video without a row.
KILL_TOLERANCE_FRAMES = 1


@dataclass
class VideoCheck:
    video: Path
    frames: Optional[int]  # counted from the file; None when it could not be
    rows: Optional[int]  # the CSV's rows; None without a CSV
    problems: List[str] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)  # accepted, not problems: a killed run's unlisted last frames


def run_killed(folder: Path) -> bool:
    """Whether the experiment's manifest.json says its run was killed: the end-of-run write never came (run.final
    false), or the outcome is running (killed before any stop was written) or killed (rig-sync's word for a run that
    never closed). False without a readable manifest."""
    try:
        run = json.loads((Path(folder) / "manifest.json").read_text(encoding="utf-8-sig"))["run"]
        return run.get("final") is False or run.get("outcome") in ("running", "killed")
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return False


def avi_frames(path: Path) -> int:
    """The video frames an AVI holds: its ##dc and ##db chunks, found by walking every RIFF and movi list. A list
    whose size is 0 or runs past the end (a writer that never closed it) is read to the end of the file, and a chunk
    cut short by the end is not counted."""
    size = path.stat().st_size
    with open(path, "rb") as f:

        def walk(start: int, end: int, in_movi: bool) -> int:
            count, pos = 0, start
            while pos + 8 <= end:
                f.seek(pos)
                header = f.read(8)
                tag, length = header[:4], int.from_bytes(header[4:], "little")
                if not all(32 <= b < 127 for b in tag):
                    break  # not a chunk: the rest is unreadable
                if tag in (b"RIFF", b"LIST"):
                    kind = f.read(4)
                    stop = end if length == 0 or pos + 8 + length > end else pos + 8 + length
                    count += walk(pos + 12, stop, kind == b"movi" or (in_movi and kind == b"rec "))
                    pos = stop + (length % 2 if stop == pos + 8 + length else 0)
                    continue
                if pos + 8 + length > end:
                    break  # cut short by the end of the file
                if in_movi and tag[2:] in (b"dc", b"db") and tag[:2].isdigit():
                    count += 1
                pos += 8 + length + length % 2
            return count

        return walk(0, size, False)


def decoded_frames(path: Path, ffprobe: str) -> int:
    """The frames ffprobe decodes from a video, every one in the file rather than what an index says."""
    result = subprocess.run(
        [ffprobe, "-v", "error", "-threads", "0", "-count_frames", "-select_streams", "v:0",
         "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", str(path)],
        capture_output=True, text=True,
    )
    text = result.stdout.strip().split(",")[0]
    if not text.isdigit():
        raise ValueError(f"ffprobe read no frames ({result.stderr.strip() or 'no output'})")
    return int(text)


def _rows(path: Path) -> List[dict]:
    with open(path, newline="", encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def check_videos(folder: Path, ffprobe: Optional[str] = None, killed: Optional[bool] = None) -> List[VideoCheck]:
    """Every camera video in an experiment folder (<Device>/<Device>_*.mkv or .avi), each with its problems and notes.
    killed: whether the run was killed; None reads it from the folder's manifest.json."""
    folder = Path(folder)
    ffprobe = ffprobe or shutil.which("ffprobe")
    killed = run_killed(folder) if killed is None else killed
    checks: List[VideoCheck] = []
    for device in sorted(p for p in folder.iterdir() if p.is_dir()):
        videos = sorted(p for p in device.iterdir() if p.suffix.lower() in VIDEO_SUFFIXES and p.is_file())
        last_id: Optional[int] = None
        for video in videos:
            name = f"{device.name}/{video.name}"
            check = VideoCheck(video, None, None)
            checks.append(check)
            try:
                if video.suffix.lower() == ".avi":
                    check.frames = avi_frames(video)
                elif ffprobe is None:
                    check.problems.append(f"{name}: frames not counted, ffprobe is not on PATH")
                else:
                    check.frames = decoded_frames(video, ffprobe)
            except (OSError, ValueError) as e:
                check.problems.append(f"{name}: frames could not be counted: {e}")
            error = video.with_suffix(".error.txt")
            if error.is_file():
                check.problems.append(f"{name}: the writer reported: {error.read_text(encoding='utf-8', errors='replace').strip()}")
            table = video.with_suffix(".csv")
            if not table.is_file():
                check.problems.append(f"{name}: no {table.name} beside it")
                continue
            rows = _rows(table)
            check.rows = len(rows)
            unlisted = 0  # a killed run's frames in the video beyond its CSV, accepted as a note
            if check.frames is not None and check.frames != check.rows:
                extra = check.frames - check.rows
                # A killed run leaves the last video, and the one before it when the kill came during an hour split's
                # handover, ahead of its CSV.
                if killed and video in videos[-2:] and 0 < extra <= KILL_TOLERANCE_FRAMES:
                    check.notes.append(f"{name}: killed run: {extra} frames at the end have no CSV row")
                    unlisted = extra
                else:
                    check.problems.append(f"{name}: holds {check.frames} frames, but {table.name} lists {check.rows}"
                                          + (f" ({-extra} missing from the video)" if extra < 0 else ""))
            column = next((c for c in FRAME_ID_COLUMNS if rows and c in rows[0]), None)
            if column is None:
                if rows:
                    check.notes.append(f"{name}: {table.name} has no frame id column ({', '.join(FRAME_ID_COLUMNS)}),"
                                       " so frames lost by the camera are not checked")
                continue
            steps = 0
            for row in rows:
                try:
                    frame_id = int(float(row[column]))
                except (TypeError, ValueError):
                    check.problems.append(f"{name}: {table.name} has a {column} that is not a number: {row[column]!r}")
                    break
                if last_id is not None and frame_id != last_id + 1:
                    steps += 1
                    if steps <= 3:
                        check.problems.append(f"{name}: {column} goes from {last_id} to {frame_id}"
                                              + (f" ({frame_id - last_id - 1} frames lost)" if frame_id > last_id else ""))
                last_id = frame_id
            if last_id is not None:
                last_id += unlisted  # the next video's first frame follows the ones this CSV never listed
            if steps > 3:
                check.problems.append(f"{name}: {steps - 3} more {column} steps other than 1")
    return checks


def video_problems(folder: Path, ffprobe: Optional[str] = None) -> List[str]:
    """Every problem with an experiment's videos, each a sentence; none when every video holds every frame its CSV lists."""
    return [p for check in check_videos(folder, ffprobe) for p in check.problems]


def describe(check: VideoCheck) -> str:
    frames = "not counted" if check.frames is None else f"{check.frames} frames"
    rows = "no CSV" if check.rows is None else f"CSV {check.rows} rows"
    return f"  {check.video.parent.name}/{check.video.name}: {frames}, {rows}"


def main() -> None:
    parser = argparse.ArgumentParser(description="Whether each camera video in an experiment holds every frame its CSV lists.")
    parser.add_argument("folders", nargs="+", type=Path, help="experiment folders (ses-...)")
    args = parser.parse_args()
    failed = False
    for folder in args.folders:
        checks = check_videos(folder)
        print(f"{folder.name}: {len(checks)} videos")
        for check in checks:
            print(describe(check))
            for note in check.notes:
                print(f"  NOTE {note}")
            for problem in check.problems:
                print(f"  PROBLEM {problem}")
            failed |= bool(check.problems)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
