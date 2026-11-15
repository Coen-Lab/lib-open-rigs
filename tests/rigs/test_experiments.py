import json
from dataclasses import dataclass
from pathlib import Path

from open_rigs.rigs.experiments import LIVE_DATA, ROOTS, find, unanswered, written_by


@dataclass
class Experiment:
    name: str
    when: str


def read(folder: Path):
    if (folder / "manifest.json").is_file() and not (folder / "BonsaiData").is_dir():
        return None, True
    return Experiment(folder.name, (folder / "when.txt").read_text()), False


def experiment(root: Path, name: str, when: str = "", manifest: bool = False, data: bool = True) -> Path:
    folder = root / "sub-GN012" / name
    folder.mkdir(parents=True)
    if when:
        (folder / "when.txt").write_text(when)
    if manifest:
        (folder / "manifest.json").write_text(json.dumps({"source": {"repo": "ucl-open/coen-dome-navigation"}}))
    if data:
        (folder / "BonsaiData").mkdir()
    return folder


def test_every_root_is_searched_newest_first_and_a_folder_seen_twice_counts_once(tmp_path):
    rig, staging = tmp_path / "rig", tmp_path / "staging"
    experiment(rig, "ses-002", "2026-11-14")
    experiment(staging, "ses-002", "2026-11-14")
    experiment(staging, "ses-001", "2026-11-13")
    unread: list[str] = []
    found = find("GN012", [([rig], read), ([staging], read)], unread)
    assert unread == [] and [e.name for e in found] == ["ses-002", "ses-001"]


def test_an_unreadable_or_manifest_only_experiment_and_a_silent_share_are_said(tmp_path):
    experiment(tmp_path, "ses-003")  # no when.txt: unreadable
    experiment(tmp_path, "ses-004", manifest=True, data=False)
    unread: list[str] = []
    assert find("GN012", [([tmp_path, Path(r"\\no-such-server-for-a-test\share")], read)], unread) == []
    assert unread == [
        "ses-003 (Unreadable: FileNotFoundError)",
        r"\\no-such-server-for-a-test\share\ (No Answer)",
        "ses-004 (Only Its Manifest Is Left)",
    ]


def test_a_manifest_names_its_repo_and_an_unreadable_one_counts(tmp_path):
    manifest = tmp_path / "manifest.json"
    manifest.write_text(json.dumps({"source": {"repo": "ucl-open/av-dome"}}))
    assert written_by(manifest, ["coen-dome-decision-making", "av-dome"])
    assert not written_by(manifest, ["coen-dome-navigation"])
    manifest.write_text("{")
    assert written_by(manifest, ["coen-dome-navigation"])


def test_the_roots_are_the_rig_staging_live_data_and_production_and_a_missing_local_folder_has_none(tmp_path):
    assert LIVE_DATA in ROOTS and Path(r"C:\data") in ROOTS
    assert not unanswered(tmp_path / "missing")
