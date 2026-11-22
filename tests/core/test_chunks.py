"""open_rigs.chunks: a reader takes only <Name>_<time><suffix>, the name matched exactly."""

import re
from datetime import datetime
from pathlib import Path

import pytest

from open_rigs import chunks


def test_only_the_exact_name_and_a_time_are_chunks(tmp_path):
    names = ["Behavior_32_2026-11-14T10-00-00.bin", "Behavior_32_2026-11-14T11-00-00.bin",  # chunks
             "Behavior_32_2026-11-14T10-00-00_crf35.bin", "Behavior_32_x.bin", "Behavior_32_1.bin",  # skipped
             "Behavior_320_2026-11-14T10-00-00.bin", "BehaviorX32_2026-11-14T10-00-00.bin",  # another name
             "Behavior_32_2026-11-14T10-00-00.csv", "Behavior_device.json", "Behavior.yml"]  # another suffix
    for name in names:
        (tmp_path / name).write_bytes(b"")
    read, skipped = chunks.files(tmp_path, "Behavior_32", ".bin")
    assert [p.name for p in read] == names[:2]
    assert sorted(p.name for p in skipped) == sorted(names[2:5])
    assert chunks.files(tmp_path / "absent", "Behavior_32", ".bin") == ([], [])


def test_the_time_is_formatdates():
    # OpenRigs.Core's FormatDate: local time to the second, then the offset as ±HH-MM.
    source = (Path(__file__).parents[2] / "src/OpenRigs.Core/FormatDate.cs").read_text(encoding="utf-8")
    assert 'LocalFormat = "yyyy-MM-ddTHH-mm-ss"' in source and 'ToString("hh\\\\-mm"' in source
    for token in ("2026-11-14T10-00-00", "2026-11-20T14-32-56+00-00", "2026-11-20T07-32-56-07-00"):
        assert re.fullmatch(chunks.TIME, token)
    for token in ("2026-11-14T10:00:00", "2026-11-14T10-00-00+0100", "2026-11-14T10-00-00Z"):
        assert not re.fullmatch(chunks.TIME, token)


@pytest.mark.parametrize("token, utc", [
    ("2026-11-20T14-32-56+00-00", "2026-11-20T14:32:56+00:00"),
    ("2026-07-20T15-32-56+01-00", "2026-07-20T14:32:56+00:00"),
    ("2026-11-20T07-32-56-07-00", "2026-11-20T14:32:56+00:00"),
    ("2026-11-14T10-00-00", "2026-11-14T10:00:00+00:00"),  # an old name: UTC
])
def test_parse_time_gives_the_instant(token, utc):
    at = chunks.parse_time(token)
    assert at.tzinfo is not None and at == datetime.fromisoformat(utc)


def test_parse_time_refuses_other_text():
    with pytest.raises(ValueError):
        chunks.parse_time("2026-11-14T10:00:00")


def test_chunks_come_in_time_order_not_name_order(tmp_path):
    # October's repeated hour: the second 01-30 chunk (+00-00) is later than the first (+01-00), and an old UTC name
    # from before sorts first whatever its text.
    names = ["Behavior_32_2026-10-25T01-30-00+00-00.bin", "Behavior_32_2026-10-25T01-30-00+01-00.bin", "Behavior_32_2026-10-24T23-00-00.bin"]
    for name in names:
        (tmp_path / name).write_bytes(b"")
    read, _ = chunks.files(tmp_path, "Behavior_32", ".bin")
    assert [p.name for p in read] == [names[2], names[1], names[0]]
    assert chunks.time_of(read[1]) < chunks.time_of(read[2])
