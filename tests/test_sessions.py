"""open_rigs.sessions: one workflow's sessions found by repository name, and each session's task."""

import json

from open_rigs import sessions


def session(root, mouse, name, repo, task=None):
    folder = root / f"sub-{mouse}" / name
    (folder / "Settings").mkdir(parents=True)
    (folder / "manifest.json").write_text(json.dumps({"source": {"repo": repo}}), encoding="utf-8")
    if task is not None:
        (folder / "Settings" / f"Settings_Task_{name[-6:]}.json").write_text(json.dumps({"taskParameters": task}), encoding="utf-8")
    return folder


def test_sessions_are_found_by_repository_name_whatever_the_owner(tmp_path):
    a = session(tmp_path, "AB001", "ses-001_20261101T090000", "https://github.com/example/task-a.git")
    session(tmp_path, "AB001", "ses-002_20261102T090000", "https://github.com/example/task-b")
    b = session(tmp_path, "CD002", "ses-001_20261103T090000", "https://github.com/someone/Task-A/")
    assert sessions.find_sessions(tmp_path, "task-a") == [a, b]
    assert sessions.find_sessions(tmp_path, "task-a", subjects=["CD002"]) == [b]
    assert sessions.load_sessions(tmp_path, "nothing").empty


def test_a_session_s_stage_conditions_and_weights_come_from_its_task_file(tmp_path):
    task = {"curriculum": {"code": "S03A"}, "trialConditions": [
        {"trialType": "visual", "weight": 2}, {"trialType": "auditory", "weight": 1, "set": {"a": 1}}, {"trialType": "visual", "weight": 3}]}
    s = session(tmp_path, "AB001", "ses-001_20261101T090000", "r", task)
    assert sessions.curriculum_stage(s) == {"code": "S03A"}
    assert sessions.trial_conditions(s)[0]["set"] == {} and sessions.trial_conditions(s)[1]["set"] == {"a": 1}
    assert sessions.trial_weights(s) == {"visual": 5, "auditory": 1}
    bare = session(tmp_path, "AB001", "ses-002_20261102T090000", "r")
    assert sessions.curriculum_stage(bare) is None and sessions.trial_weights(bare) is None
