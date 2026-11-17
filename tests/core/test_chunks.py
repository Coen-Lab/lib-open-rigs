"""open_rigs.chunks: a reader takes only <Name>_<time><suffix>, the name matched exactly."""

import re
from pathlib import Path

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
    # OpenRigs.Core's FormatDate: the round-trip format with : made -, cut at the fraction of a second.
    source = (Path(__file__).parents[2] / "src/OpenRigs.Core/FormatDate.cs").read_text(encoding="utf-8")
    assert 'ToString("o").Replace(\':\', \'-\')' in source and "IndexOf('.')" in source
    assert re.fullmatch(chunks.TIME, "2026-11-14T10-00-00") and not re.fullmatch(chunks.TIME, "2026-11-14T10:00:00")
