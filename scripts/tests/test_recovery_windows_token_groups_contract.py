"""GD-31J.27bt source-only guard. NOT Windows token/API attestation."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TOKEN = OFFICE / "RecoveryWindowsTokenGroupProbe.cs"
ACL = OFFICE / "RecoveryWindowsAclRiskObserver.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
COORD = OFFICE / "RecoverySharedVolumeCoordinator.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryWindowsTokenGroupEvidenceTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class WindowsTokenGroupContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.token = TOKEN.read_text(encoding="utf-8")
        cls.acl = ACL.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.coord = COORD.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_existing_token_handle_only_with_bounded_information_call(self):
        for name in ("ObserveExistingToken(", "SafeFileHandle token",
                     "GetTokenInformation(", "TokenGroupsClass = 2",
                     "MaxGroupCount = 256", "MaxBufferBytes = 128 * 1024",
                     "ErrorInsufficientBuffer = 122", "Marshal.FreeHGlobal(buffer)",
                     "ConvertSidToStringSidW", "LocalFree(text)"):
            with self.subTest(name=name):
                self.assertIn(name, self.token)
        for name in ("OpenProcessToken(", "OpenThreadToken(", "AdjustTokenGroups(",
                     "AdjustTokenPrivileges(", "Impersonate", "CreateRestrictedToken("):
            with self.subTest(forbidden=name):
                self.assertNotIn(name, self.token)

    def test_denial_only_classifies_enabled_disabled_denyonly_and_unknown(self):
        for name in ("GroupDenyOnly = 0x00000010", "GroupEnabled = 0x00000004",
                     "RecoveryWindowsTokenGroupState.Unreviewed",
                     "RecoveryWindowsTokenGroupState.DenyOnly",
                     "RecoveryWindowsTokenGroupState.Disabled",
                     "RecoveryWindowsTokenAceFinding.UnresolvedTokenCoverage",
                     "InheritOnlyNotApplicableToObject",
                     "IsSidGrammar("):
            self.assertIn(name, self.token)

    def test_correlation_is_bounded_and_never_authorizes(self):
        self.assertIn("Correlate(", self.token)
        self.assertIn("if (acl.Aces is null", self.token)
        self.assertIn("Array.AsReadOnly(groups.ToArray())", self.token)
        self.assertIn("internal bool CanAuthorizeCapture => false;", self.token)
        self.assertEqual(2, self.token.count("CanAuthorizeCapture => false;"))

    def test_acl_records_preserve_inheritance_flags_without_writing(self):
        self.assertIn("byte Flags = 0", self.acl)
        self.assertIn("Marshal.ReadByte(address, 1)", self.acl)
        self.assertIn("Array.AsReadOnly(aces.ToArray())", self.acl)
        self.assertIn("internal bool CanAuthorizeCapture => false;", self.acl)

    def test_no_filesystem_mutation_or_production_wiring(self):
        for text in (self.store, self.coord):
            self.assertNotIn("RecoveryWindowsTokenGroupProbe.", text)
        for forbidden in ("File.Delete(", "Directory.Delete(", "File.Move(",
                          "File.Write", "SetSecurityInfo(", "AccessCheck(",
                          "CreateFileW(", "OpenProcessToken("):
            self.assertNotIn(forbidden, self.token)

    def test_twelve_unexecuted_mstest_cases_and_manual_gates(self):
        self.assertEqual(12, self.tests.count("[TestMethod]"))
        for name in ("DenyOnlyGroupCannotQualifyAnAllowAce",
                     "InheritOnlyAceDoesNotApplyToCurrentDirectoryButCannotProveSafety",
                     "CorrelationCountsDirectTokenGroupMatchesWithoutGrantingAccess",
                     "MissingAclOrTokenEvidenceCannotCreateAPositiveFinding"):
            self.assertIn(name, self.tests)
        for gate in ("gd31j27bt-windows-live-tokengroups-manual-e2e",
                     "gd31j27bt-windows-restricted-impersonation-manual-e2e",
                     "gd31j27bt-windows-inherited-ace-mic-privilege-manual-e2e"):
            self.assertIn(gate, self.runner)
        name = 'CommandStep("recovery-windows-token-groups-source-tests"'
        self.assertEqual(1, self.runner.count(name))
        self.assertLess(self.runner.index(name),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
