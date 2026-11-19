"""The workflow checks every open-rigs task repository runs on its src\\main.bonsai, as pytest tests. A repository's
tests\\test_workflow.py takes them all with `globals().update(workflow_checks.tests(ROOT))` (ROOT its folder) and keeps
only its own; each test has the name the repositories' copies had."""

import re
from collections.abc import Callable
from functools import cache
from pathlib import Path

import yaml

import open_rigs.rigs
from open_rigs.rigs import workflow_lint as lint
from open_rigs.rigs.lab_rig import LabRig


def tests(root: Path) -> dict[str, Callable[[], None]]:
    """Each shared check as a test of the repository at `root`, by its test name."""
    root = Path(root)

    @cache
    def workflow() -> lint.Workflow:
        return lint.Workflow(root)

    def text() -> str:
        return (root / "src" / "main.bonsai").read_text(encoding="utf-8-sig")

    def test_every_config_root_is_the_lab_s():
        # check-workflow holds every node that reads rig.yml to one root; this holds that root to the lab's.
        assert set(re.findall(r"<(?:\w+:)?ConfigRoot>([^<]*)</", text())) == {r"C:\Coen-Lab-Local\RigConfigs"}

    def test_no_fixed_width_pulse_reaches_a_reward_valve():
        # Every reward is its RewardValve piece's calibrated pulse; checked against the lab's rig template's valve lines.
        template = (Path(open_rigs.rigs.__file__).parent / "rig-template.yml").read_text(encoding="utf-8").replace("CHANGE-ME", "X")
        rig = LabRig.model_validate(yaml.safe_load(template))
        assert rig.reward_valve_lines() and lint.fixed_width_pulses_on_reward_valves(workflow(), rig.reward_valve_lines()) == []

    def test_no_event_sender_completes_events():
        senders, finite = lint.finite_event_senders(workflow())
        assert senders == 0 and finite == []

    def test_no_trial_graph_closes_a_shared_subject():
        assert lint.subjects_closed_by_a_nested_multicast(workflow()) == []

    def test_every_command_is_declared_once_by_the_task_or_its_piece():
        # The pieces declare their own (LickSpoutStage's spout commands, each RewardValve's <Name>.Reward): a task's copy
        # stops ControlServer at Start.
        assert lint.undeclared_commands(workflow()) == [] and lint.declared_twice(workflow()) == []

    def test_the_extensions_readme_names_every_public_class():
        folder = root / "src" / "Extensions"
        readme = (folder / "README.md").read_text(encoding="utf-8-sig")
        code = "".join(f.read_text(encoding="utf-8-sig") for f in folder.glob("*.cs") if not f.name.endswith(".Generated.cs"))
        classes = set(re.findall(r"^public (?:static )?class (\w+)", code, re.M))
        assert sorted(c for c in classes if f"`{c}`" not in readme) == []

    checks = {
        "test_zapit_is_the_shared_include_given_the_conditions_file": lint.zapit_include_problems,
        "test_the_live_view_is_last_and_fed_by_nothing": lint.live_view_problems,
        "test_the_penalty_sound_is_the_lab_s_at_the_task_s_level": lint.penalty_sound_problems,
        "test_the_readme_lists_every_open_rigs_block": lint.readme_missing_blocks,
        "test_spout_step_size_is_the_lick_spout_stage_s": lint.spout_step_size_problems,
        "test_the_experiment_records_are_the_shared_include": lint.experiment_records_problems,
        "test_the_launch_settings_are_the_shared_include": lint.launch_settings_problems,
        "test_the_layout_is_tracked": lint.layout_problems,
        "test_the_experiment_logs_are_the_lab_s": lint.experiment_log_problems,
        "test_every_file_the_workflow_writes_is_in_the_manifest_s_expected_files": lint.expected_files_problems,
        "test_the_task_logs_are_the_shared_include": lint.task_logs_problems,
        "test_the_hardware_is_the_lab_s_pieces": lint.hardware_problems,
    }

    def no_problems(check: Callable[[lint.Workflow], list[str]], name: str) -> Callable[[], None]:
        def test():
            assert check(workflow()) == []

        test.__name__ = test.__qualname__ = name
        return test

    found = {name: no_problems(check, name) for name, check in checks.items()}
    found.update({f.__name__: f for f in (
        test_every_config_root_is_the_lab_s, test_no_fixed_width_pulse_reaches_a_reward_valve, test_no_event_sender_completes_events,
        test_no_trial_graph_closes_a_shared_subject, test_every_command_is_declared_once_by_the_task_or_its_piece,
        test_the_extensions_readme_names_every_public_class)})
    return found
