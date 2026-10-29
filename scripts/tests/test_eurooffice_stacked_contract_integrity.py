"""Regression gate for stacked EuroOffice Desktop lab source contracts.

This test only reads files; no Qt/CEF/.NET processes, no network and no documents.
The older GD-31J.15/16 contracts must remain valid once GD-31J.17/18 are stacked.
"""
from pathlib import Path
import ast
import unittest


REPO_ROOT = Path(__file__).resolve().parents[2]
CEF_LAB = REPO_ROOT / "probes/eurooffice-desktop/cef_lab"


class EuroOfficeStackedContractIntegrityTests(unittest.TestCase):
    def test_cross_directory_source_tests_resolve_repository_root(self):
        for filename in (
            "test_osr_click_contract.py",
            "test_osr_keyboard_contract.py",
            "test_osr_ime_commit_contract.py",
            "test_avalonia_ime_caret_contract.py",
        ):
            with self.subTest(filename=filename):
                test_file = CEF_LAB / filename
                tree = ast.parse(test_file.read_text(encoding="utf-8"), filename=str(test_file))
                root_assignments = [
                    item for item in tree.body
                    if isinstance(item, ast.Assign)
                    and any(isinstance(target, ast.Name) and target.id == "ROOT"
                            for target in item.targets)
                ]
                self.assertEqual(1, len(root_assignments))
                # From probes/eurooffice-desktop/cef_lab/*.py, parents[3]
                # is the repository; parents[2] incorrectly points to probes/.
                self.assertEqual(
                    "Path(__file__).resolve().parents[3]",
                    ast.unparse(root_assignments[0].value),
                )
                self.assertEqual(REPO_ROOT, test_file.resolve().parents[3])

    def test_click_only_gate_does_not_forbid_later_keyboard_feature(self):
        source = (CEF_LAB / "osr_input.cpp").read_text(encoding="utf-8")
        self.assertIn('command == "left-down"', source)
        self.assertIn('command == "nav-key"', source)
        click_section = source.split('command == "left-down"', 1)[1].split(
            'command == "nav-key"', 1)[0]
        self.assertNotIn("SendKeyEvent(", click_section)

    def test_staged_host_accepts_cumulative_opt_in_flags(self):
        source = (CEF_LAB / "cef_staged_open.cpp").read_text(encoding="utf-8")
        self.assertIn("OsrInputPump inputPump(streamSession, streamTab, clickLab,", source)
        self.assertIn("keyboardLab, imeCommitLab);", source)
        self.assertIn("#ifndef GETDOC_GD31J17_IME_COMMIT_LAB", source)


if __name__ == "__main__":
    unittest.main()
