"""GD-31J.27bs: source-only review contract (NOT Windows runtime attestation)."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src" / "GetDocument.Office" / "LibreOffice"
SOURCE = OFFICE / "RecoveryWindowsAclRiskObserver.cs"
TESTS = ROOT / "tests" / "GetDocument.Office.Tests" / "RecoveryWindowsAclRiskObserverTests.cs"
RUNNER = ROOT / "scripts" / "office_acceptance.py"


class WindowsAclRiskSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")
        cls.coord = (OFFICE / "RecoverySharedVolumeCoordinator.cs").read_text(encoding="utf-8")

    def test_native_inspection_from_existing_handle_only(self):
        for token in ("ObserveExistingHandle(", "SafeFileHandle handle",
                      "GetSecurityInfo(handle,", "GetAclInformation(",
                      "GetAce(", "ConvertSidToStringSidW(",
                      "LocalFree(descriptor)", "MaxAceCount = 256"):
            with self.subTest(token=token):
                self.assertIn(token, self.source)

    def test_fails_closed_for_null_dacl_unknown_aces_and_bad_sid(self):
        for token in ("if (acl == 0)", "InvalidDataException(",
                      "UnreviewedAce", "AccessDeniedAceType",
                      "size < 16", "subAuthorities > 15",
                      "BroadGroupWriteAllow"):
            self.assertIn(token, self.source)

    def test_explicit_readonly_evidence_never_grants_write_delete(self):
        self.assertIn("internal bool CanAuthorizeCapture => false;", self.source)
        for forbidden in ("File.Delete(", "Directory.Delete(", "File.Move(",
                          "File.Write", "FileStream(", "SetSecurityInfo(",
                          "SetNamedSecurityInfo(", "File.SetAccessControl("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.source)

    def test_p0_modules_are_still_not_wired_to_production(self):
        self.assertNotIn("RecoveryWindowsAclRiskObserver.", self.store)
        self.assertNotIn("RecoveryWindowsAclRiskObserver.", self.coord)

    def test_seven_regressions_cover_conservative_acl_observation(self):
        self.assertEqual(7, self.tests.count("[TestMethod]"))
        for name in ("BroadWellKnownTrusteesWithWriteRightsAreFlagged",
                     "ReadOnlyBroadGroupDoesNotMakeDaclTrusted",
                     "UnsupportedCallbackObjectOrMalformedSidStaysUnreviewed",
                     "DenyAceDoesNotEraseAnAllowRiskWithoutEffectiveRightsAnalysis",
                     "ReadingExistingWindowsHandleNeverApprovesCaptureOrMutatesFile"):
            self.assertIn(name, self.tests)

    def test_registered_before_offline_restore_and_manual_gates_block(self):
        name = 'CommandStep("recovery-windows-acl-risk-source-tests"'
        self.assertEqual(1, self.runner.count(name))
        self.assertLess(self.runner.index(name),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in ("gd31j27bs-windows-ntfs-dacl-ace-manual-e2e",
                     "gd31j27bs-windows-effective-token-inheritance-manual-e2e",
                     "gd31j27bs-windows-reparse-owner-race-manual-e2e"):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
