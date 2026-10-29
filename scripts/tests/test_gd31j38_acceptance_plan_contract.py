"""GD-31J.38 source-only check for the deferred offline acceptance command plan.

This file reads source only. It never invokes dotnet, CEF, Qt, Python test
discovery, shell commands, network requests or actual repository cleanup.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "scripts/office_acceptance.py"
TESTS = ROOT / "scripts/tests/test_office_acceptance.py"

class Gd31J38OfflinePlanContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")

    def test_plan_validated_before_restore_sdk_or_any_test_execution(self):
        src = self.runner
        self.assertIn("def validate_step_plan(root: Path, steps: list[CommandStep])", src)
        self.assertIn("validate_step_plan(root, planned)", src)
        self.assertIn("validate_step_plan(root, steps)", src)
        self.assertLess(src.index("validate_step_plan(root, steps)"),
                        src.index('report["dotnetSdk"] = ensure_dotnet10()'))
        self.assertIn('offlineStepTargetsVerified',src)
        self.assertIn('linuxPlanStepCount',src)
        self.assertIn('windowsPlanStepCount',src)
        self.assertIn('releaseReady": False',src)
        self.assertIn('status": "not-run"',src)

    def test_allowed_commands_and_source_targets_are_bounded(self):
        for marker in (
            'cmd[0] == sys.executable',
            '"-m", "unittest", "discover"',
            'candidate.is_absolute()',
            'full.is_relative_to(base)',
            'not full.is_file()',
            'cmd[0] == "dotnet"',
            'cmd[1] not in ("restore", "build", "test")',
            'cmd[3] != "--source"',
            '"-p:NuGetAudit=false"',
            '"--no-restore"',
            '"--minimum-expected-tests"',
            'step.name in seen',
            'if restore >= build:',
        ):
            self.assertIn(marker,self.runner)
        self.assertNotIn('shell=True', self.runner)

    def test_regressions_cover_negative_and_real_plan_cases(self):
        for name in (
            'test_valid_local_only_steps_have_existing_targets_and_no_commands_are_run',
            'test_missing_contract_or_project_is_blocking_before_execution',
            'test_rejects_duplicate_step_names_and_unapproved_commands',
            'test_rejects_path_escape_or_network_source_even_when_target_exists',
            'test_rejects_missing_no_restore_and_tests_before_solution_build',
            'test_all_registered_windows_linux_source_gates_are_checked_before_restore',
        ):
            self.assertIn(name,self.tests)

if __name__ == "__main__":
    unittest.main()
