"""GD31J27i: offline-only source contracts for independent review gate.

The gate must remain read-only and NEVER become a SaveAs/merge authorization
based on JSON flags or a successful stdout receipt.
"""
from pathlib import Path
import sys
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]
sys.path.insert(0, str(LAB))
sys.path.insert(0, str(ROOT / "scripts"))
import native_saveas_acceptance_gate as gate
import office_acceptance as acceptance


class NativeSaveAsAcceptanceSourceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gate = (LAB / "native_saveas_acceptance_gate.py").read_text("utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf-8")
        cls.runner = (ROOT / "scripts/office_acceptance.py").read_text("utf-8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf-8")

    def test_all_native_stages_are_explicit_and_nonoptional_manual_gates(self):
        self.assertEqual(16, len(gate.REQUIRED_MANUAL_GATES))
        self.assertEqual(len(gate.REQUIRED_MANUAL_GATES),
                         len(set(gate.REQUIRED_MANUAL_GATES)))
        self.assertTrue(set(gate.REQUIRED_MANUAL_GATES).issubset(
            acceptance.MANUAL_GATES))
        self.assertIn("eurooffice-gd31j27h-v1-v2-native-ooxml-crash-lineage-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27j-fd-bound-link-no-path-swap-e2e",
                      gate.REQUIRED_MANUAL_GATES)
        self.assertIn("eurooffice-gd31j27k-owner-only-unlinked-snapshot-same-uid-writer-e2e",
                      gate.REQUIRED_MANUAL_GATES)

    def test_all_fault_scenarios_are_required_and_machine_readable(self):
        self.assertEqual(19, len(gate.CASES))
        self.assertEqual(len(gate.CASES), len(set(gate.CASES)))
        for fault in ("source-name-swap-during-linkat",
                      "file-fsync-denied-before-linkat",
                      "directory-fsync-denied-after-linkat",
                      "process-killed-after-linkat-before-dir-fsync",
                      "unrecognized-files-symlinks-never-deleted"):
            self.assertIn(fault, gate.CASES)
        self.assertIn("same-uid-writer-after-anonymous-snapshot", gate.CASES)
        self.assertIn("REQUIRED_AUTOMATED_STEPS", self.gate)

    def test_even_all_claims_cannot_enable_production_or_release(self):
        for mark in ('"releaseReady": False',
                     '"approvedForProduction": False',
                     '"writePermissionGranted": False',
                     '"safeToDeleteAutomatically": False',
                     '"nativeSaveProven": False',
                     '"documentVerified": False',
                     '"saveVerified": False'):
            self.assertIn(mark, self.gate)
        self.assertIn('return 2', self.gate)
        self.assertNotIn("subprocess.", self.gate)
        self.assertNotIn("os.unlink(", self.gate)
        self.assertNotIn("os.replace(", self.gate)
        self.assertNotIn("shutil.rmtree(", self.gate)
        self.assertNotIn("open(path, 'w')", self.gate)

    def test_opt_in_native_build_remains_default_off(self):
        self.assertIn(
            'option(GETDOC_GD31J26_ENABLE_NATIVE_SAVEAS_LAB '
            '"Isolated synthetic SaveAs via exact vendor event" OFF)', self.cmake)
        self.assertIn(
            'option(GETDOC_GD31J27C_ENABLE_NATIVE_EVENT_V2_LAB '
            '"Pinned native SaveAs event v2 diagnostic for isolated synthetic VM" OFF)',
            self.cmake)
        self.assertIn("GETDOC_GD31J27C_EVENT_V2_ISOLATION_CONFIRMED", self.cmake)
        self.assertIn("GETDOC_GD31J26_PRIVATE_OUTPUT_ISOLATION_CONFIRMED", self.cmake)
        self.assertIn("NativeSaveAsPublish(", self.policy)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
