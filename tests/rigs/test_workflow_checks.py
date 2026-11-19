"""workflow_checks: the shared workflow tests a task repository takes into its own test_workflow.py."""

import pytest

from open_rigs.rigs import workflow_checks


def test_each_shared_check_is_a_test_named_as_the_repositories_copies(tmp_path):
    tests = workflow_checks.tests(tmp_path)
    assert len(tests) == 18 and all(name.startswith("test_") and test.__name__ == name for name, test in tests.items())


def test_a_check_reads_the_repository_it_was_given(tmp_path):
    (tmp_path / "src").mkdir()
    main = tmp_path / "src" / "main.bonsai"
    main.write_text('<WorkflowBuilder><ConfigRoot>C:\Coen-Lab-Local\RigConfigs</ConfigRoot></WorkflowBuilder>', encoding="utf-8")
    workflow_checks.tests(tmp_path)["test_every_config_root_is_the_lab_s"]()
    main.write_text("<WorkflowBuilder><ConfigRoot>D:\Elsewhere</ConfigRoot></WorkflowBuilder>", encoding="utf-8")
    with pytest.raises(AssertionError):
        workflow_checks.tests(tmp_path)["test_every_config_root_is_the_lab_s"]()
