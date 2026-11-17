"""open_rigs.rigs.video_frames: each camera video's frames, counted from the file, against its CSV."""

import json
import shutil
import subprocess
import sys

import pytest

from open_rigs.rigs import video_frames
from open_rigs.rigs.video_frames import avi_frames, check_videos

FFMPEG = shutil.which("ffmpeg")
needs_ffmpeg = pytest.mark.skipif(FFMPEG is None or shutil.which("ffprobe") is None, reason="ffmpeg and ffprobe are not on PATH")


def video_problems(folder):
    return [p for check in check_videos(folder) for p in check.problems]


def _chunk(tag: bytes, data: bytes, size=None) -> bytes:
    length = len(data) if size is None else size
    return tag + length.to_bytes(4, "little") + data + (b"\0" if len(data) % 2 else b"")


def _list(tag: bytes, kind: bytes, children: bytes, size=None) -> bytes:
    return _chunk(tag, kind + children, size)


def _frames(n: int, size=5) -> bytes:
    return b"".join(_chunk(b"00dc", bytes(size)) for _ in range(n))


def _avi(first: int, more: int = 0, finished: bool = True) -> bytes:
    """An OpenDML AVI: a RIFF AVI with header and index, then a RIFF AVIX; unfinished, the AVIX and its movi list
    declare size 0, as a writer killed before closing them leaves them."""
    hdrl = _list(b"LIST", b"hdrl", _chunk(b"avih", bytes(56)))
    movi = _list(b"LIST", b"movi", _frames(first) + _chunk(b"ix00", bytes(24)))
    data = _list(b"RIFF", b"AVI ", hdrl + movi + _chunk(b"idx1", bytes(16 * first)))
    if more:
        movi = _frames(more)
        data += _list(b"RIFF", b"AVIX", _list(b"LIST", b"movi", movi, None if finished else 0), None if finished else 0)
    return data


def test_avi_frames_counts_every_frame_chunk(tmp_path):
    path = tmp_path / "v.avi"
    path.write_bytes(_avi(7, 4))
    assert avi_frames(path) == 11


def test_avi_frames_reads_an_unfinished_avi_to_its_end(tmp_path):
    # The index says 7, the unfinished segment holds 4 more and a fifth cut short by the end of the file.
    path = tmp_path / "v.avi"
    path.write_bytes(_avi(7, 4, finished=False) + _chunk(b"00dc", bytes(10))[:12])
    assert avi_frames(path) == 11


def test_avi_frames_counts_frames_in_rec_lists_and_not_audio(tmp_path):
    movi = _list(b"LIST", b"movi", _list(b"LIST", b"rec ", _frames(2) + _chunk(b"01wb", bytes(8))) + _frames(1))
    path = tmp_path / "v.avi"
    path.write_bytes(_list(b"RIFF", b"AVI ", movi))
    assert avi_frames(path) == 3


def _camera(folder, name, video_bytes=None, rows=None, column="frameId", first_id=0, suffix=".avi"):
    device = folder / "Camera-Pupil"
    device.mkdir(parents=True, exist_ok=True)
    video = device / f"Camera-Pupil_{name}{suffix}"
    if video_bytes is not None:
        video.write_bytes(video_bytes)
    if rows is not None:
        lines = [f"harpTime,{column}"] + [f"{1000 + i * 0.02},{first_id + i}" for i in range(rows)]
        video.with_suffix(".csv").write_text("\n".join(lines) + "\n")
    return video


def test_a_video_holding_every_frame_its_csv_lists_has_no_problems(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(5, 3), rows=8)
    _camera(tmp_path, "2026-11-14T11-00-00", _avi(4), rows=4, first_id=8)
    checks = check_videos(tmp_path)
    assert [(c.frames, c.rows, c.problems) for c in checks] == [(8, 8, []), (4, 4, [])]


def test_a_video_short_of_its_csv_is_a_problem(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(5, 3, finished=False), rows=10, column="ChunkData.FrameID")
    assert video_problems(tmp_path) == [
        "Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: holds 8 frames, but Camera-Pupil_2026-11-14T10-00-00.csv lists 10 "
        "(2 missing from the video)"
    ]


def test_frame_id_steps_across_a_camera_s_files_are_problems(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(3), rows=3)
    _camera(tmp_path, "2026-11-14T11-00-00", _avi(2), rows=2, first_id=5)
    assert video_problems(tmp_path) == ["Camera-Pupil/Camera-Pupil_2026-11-14T11-00-00.avi: frameId goes from 2 to 5 (2 frames lost)"]


def test_a_missing_csv_and_the_writer_s_error_file_are_problems(tmp_path):
    video = _camera(tmp_path, "2026-11-14T10-00-00", _avi(3))
    video.with_suffix(".error.txt").write_text("Video x: 4 of 10 frames were dropped.\n")
    assert video_problems(tmp_path) == [
        "Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: the writer reported: Video x: 4 of 10 frames were dropped.",
        "Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: no Camera-Pupil_2026-11-14T10-00-00.csv beside it",
    ]


def test_an_mkv_without_ffprobe_is_not_counted(tmp_path, monkeypatch):
    monkeypatch.setattr(shutil, "which", lambda name: None)
    _camera(tmp_path, "2026-11-14T10-00-00", b"not a video", rows=3, suffix=".mkv")
    assert video_problems(tmp_path) == ["Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.mkv: frames not counted, ffprobe is not on PATH"]


def _mkv(path, frames):
    assert FFMPEG is not None
    subprocess.run([FFMPEG, "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=50", "-frames:v", str(frames),
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "matroska", str(path)], check=True)


@needs_ffmpeg
def test_an_mkv_s_frames_are_decoded_and_counted(tmp_path):
    video = _camera(tmp_path, "2026-11-14T10-00-00", rows=30, suffix=".mkv")
    _mkv(video, 30)
    assert [(c.frames, c.rows, c.problems) for c in check_videos(tmp_path)] == [(30, 30, [])]


@needs_ffmpeg
def test_a_truncated_mkv_is_counted_to_its_last_whole_frame(tmp_path):
    # A killed ffmpeg leaves a file cut anywhere: what is left still decodes, short of the CSV.
    video = _camera(tmp_path, "2026-11-14T10-00-00", rows=100, suffix=".mkv")
    _mkv(video, 100)
    data = video.read_bytes()
    video.write_bytes(data[: len(data) * 2 // 3])
    (check,) = check_videos(tmp_path)
    assert check.frames is not None and 30 < check.frames < 100
    assert check.problems == [f"Camera-Pupil/{video.name}: holds {check.frames} frames, but {video.stem}.csv lists 100 "
                              f"({100 - check.frames} missing from the video)"]


@needs_ffmpeg
def test_an_unreadable_mkv_is_a_problem(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", b"not a video", rows=3, suffix=".mkv")
    (problem,) = video_problems(tmp_path)
    assert problem.startswith("Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.mkv: frames could not be counted: ffprobe read no frames")


def _manifest(folder, outcome="stopped", final=False):
    # final false: the end-of-run write never came, as when Bonsai is killed after the stop's own write.
    (folder / "manifest.json").write_text(json.dumps({"run": {"outcome": outcome, "final": final}}))


def _exit_code(folder, monkeypatch):
    monkeypatch.setattr(sys, "argv", ["check-videos", str(folder)])
    with pytest.raises(SystemExit) as stop:
        video_frames.main()
    return stop.value.code


def test_a_killed_run_s_last_unlisted_frame_is_a_note(tmp_path, monkeypatch, capsys):
    # The frame in hand at the kill: in ffmpeg's stdin, its CSV row not yet flushed.
    _manifest(tmp_path)
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(101), rows=100)
    (check,) = check_videos(tmp_path)
    assert check.problems == []
    assert check.notes == ["Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: killed run: 1 frames at the end have no CSV row"]
    assert _exit_code(tmp_path, monkeypatch) == 0
    assert "NOTE Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: killed run: 1 frames" in capsys.readouterr().out


def test_a_killed_run_with_two_unlisted_frames_is_a_problem(tmp_path, monkeypatch):
    # Each row is flushed with its frame, so a second frame without one is rows lost, not the kill.
    _manifest(tmp_path, outcome="running")
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(102), rows=100)
    (check,) = check_videos(tmp_path)
    assert check.notes == []
    assert check.problems == ["Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: holds 102 frames, but "
                              "Camera-Pupil_2026-11-14T10-00-00.csv lists 100"]
    assert _exit_code(tmp_path, monkeypatch) == 1


def test_unlisted_frames_in_a_run_that_was_not_killed_are_a_problem(tmp_path, monkeypatch):
    _manifest(tmp_path, outcome="stopped", final=True)
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(101), rows=100)
    (check,) = check_videos(tmp_path)
    assert check.notes == [] and len(check.problems) == 1
    assert _exit_code(tmp_path, monkeypatch) == 1


def test_unlisted_frames_without_a_manifest_are_a_problem(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(101), rows=100)
    (check,) = check_videos(tmp_path)
    assert check.notes == [] and len(check.problems) == 1


def test_a_killed_run_s_video_short_of_its_csv_is_a_problem(tmp_path, monkeypatch):
    _manifest(tmp_path, outcome="killed", final=True)
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(90), rows=100)
    (check,) = check_videos(tmp_path)
    assert check.notes == []
    assert check.problems == ["Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: holds 90 frames, but "
                              "Camera-Pupil_2026-11-14T10-00-00.csv lists 100 (10 missing from the video)"]
    assert _exit_code(tmp_path, monkeypatch) == 1


def test_a_killed_run_s_video_before_the_last_with_unlisted_frames_is_a_note(tmp_path):
    # A kill during an hour split's handover leaves the earlier file ahead of its CSV too; the next file's frameId
    # follows the unlisted frames, so that step is no loss.
    _manifest(tmp_path)
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(101), rows=100)
    _camera(tmp_path, "2026-11-14T11-00-00", _avi(10), rows=10, first_id=101)
    first, last = check_videos(tmp_path)
    assert first.problems == [] and first.notes == [
        "Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: killed run: 1 frames at the end have no CSV row"]
    assert last.notes == [] and last.problems == []


def test_a_killed_run_s_older_video_with_unlisted_frames_is_a_problem(tmp_path):
    # Only the last two videos can be open at a kill; an older one was closed with its CSV.
    _manifest(tmp_path)
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(101), rows=100)
    _camera(tmp_path, "2026-11-14T11-00-00", _avi(10), rows=10, first_id=100)
    _camera(tmp_path, "2026-11-14T12-00-00", _avi(10), rows=10, first_id=110)
    first, second, third = check_videos(tmp_path)
    assert first.notes == [] and first.problems == [
        "Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: holds 101 frames, but Camera-Pupil_2026-11-14T10-00-00.csv lists 100"]
    assert second.problems == third.problems == []


def test_old_behaviour_box_frame_id_steps_are_problems(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(3), rows=3, column="FrameID")
    _camera(tmp_path, "2026-11-14T11-00-00", _avi(2), rows=2, column="FrameID", first_id=4)
    assert video_problems(tmp_path) == ["Camera-Pupil/Camera-Pupil_2026-11-14T11-00-00.avi: FrameID goes from 2 to 4 (1 frames lost)"]


def test_a_csv_without_a_frame_id_column_is_a_note(tmp_path):
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(3), rows=3, column="Counter")
    (check,) = check_videos(tmp_path)
    assert check.problems == [] and check.notes == [
        "Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00.avi: Camera-Pupil_2026-11-14T10-00-00.csv has no frame id column "
        "(frameId, ChunkData.FrameID, Value.ChunkData.FrameID, FrameID), so frames lost by the camera are not checked"]


def test_only_chunk_names_are_checked_and_the_rest_noted(tmp_path, capsys, monkeypatch):
    # A converted experiment keeps its video's crf copies beside it: not checked, each a note.
    _camera(tmp_path, "2026-11-14T10-00-00", _avi(3), rows=3)
    _camera(tmp_path, "2026-11-14T10-00-00_crf35", _avi(1))
    _camera(tmp_path, "x", _avi(1))
    assert [c.video.name for c in check_videos(tmp_path)] == ["Camera-Pupil_2026-11-14T10-00-00.avi"]
    notes = ["Camera-Pupil/Camera-Pupil_2026-11-14T10-00-00_crf35.avi: not checked, not named Camera-Pupil_<time>.avi",
             "Camera-Pupil/Camera-Pupil_x.avi: not checked, not named Camera-Pupil_<time>.avi"]
    assert video_frames.skipped(tmp_path) == notes
    monkeypatch.setattr(sys, "argv", ["check-videos", str(tmp_path)])
    with pytest.raises(SystemExit) as exit:
        video_frames.main()
    out = capsys.readouterr().out
    assert exit.value.code == 0 and all(f"NOTE {n}" in out for n in notes)
