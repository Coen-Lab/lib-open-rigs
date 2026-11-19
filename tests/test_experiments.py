"""open_rigs.experiments: one workflow's experiments found by repository name, and each experiment's task."""

import json

from open_rigs import experiments


def experiment(root, mouse, name, repo, task=None, trial_types=()):
    folder = root / f"sub-{mouse}" / name
    (folder / "Settings").mkdir(parents=True)
    (folder / "manifest.json").write_text(json.dumps({"source": {"repo": repo}}), encoding="utf-8")
    if task is not None:
        (folder / "Settings" / f"Settings_Task_{name[-6:]}.json").write_text(json.dumps({"taskParameters": task}), encoding="utf-8")
        rows = [{"trialCombinationId": i, "trialConditionId": i, "trialType": t} for i, t in enumerate(trial_types)]
        (folder / "Settings" / f"Settings_Combinations_{name[-6:]}.json").write_text(json.dumps({"rows": rows}), encoding="utf-8")
    return folder


def test_experiments_are_found_by_repository_name_whatever_the_owner(tmp_path):
    a = experiment(tmp_path, "AB001", "ses-001_20261101T090000", "https://github.com/example/task-a.git")
    experiment(tmp_path, "AB001", "ses-002_20261102T090000", "https://github.com/example/task-b")
    b = experiment(tmp_path, "CD002", "ses-001_20261103T090000", "https://github.com/someone/Task-A/")
    assert experiments.find_experiments(tmp_path, "task-a") == [a, b]
    assert experiments.find_experiments(tmp_path, "task-a", subjects=["CD002"]) == [b]
    assert experiments.load_experiments(tmp_path, "nothing").empty


def test_an_experiment_s_stage_conditions_and_weights_come_from_its_task_file(tmp_path):
    # The task file stores no trialType: each condition's is the one derived at Go, in Settings_Combinations' rows.
    task = {"curriculum": {"code": "S03A"}, "trialConditions": [{"weight": 2}, {"weight": 1, "set": {"a": 1}}, {"weight": 3}]}
    s = experiment(tmp_path, "AB001", "ses-001_20261101T090000", "r", task, ("visual", "auditory", "visual"))
    assert experiments.curriculum_stage(s) == {"code": "S03A"}
    assert experiments.trial_conditions(s)[1] == {"trialType": "auditory", "weight": 1, "set": {"a": 1}}
    assert experiments.trial_conditions(s)[0]["set"] == {} and experiments.trial_conditions(s)[1]["set"] == {"a": 1}
    assert experiments.trial_weights(s) == {"visual": 5, "auditory": 1}
    bare = experiment(tmp_path, "AB001", "ses-002_20261102T090000", "r")
    assert experiments.curriculum_stage(bare) is None and experiments.trial_weights(bare) is None
