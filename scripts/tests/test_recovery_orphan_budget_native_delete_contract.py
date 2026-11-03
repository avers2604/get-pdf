"""GD-31J.27bl: static source guard for orphan inventory, admission,
and disabled native deletion planning. TEXT INSPECTION ONLY; not executed
platform evidence, not C# compilation and never deletion authorization.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
BUDGET = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryCaptureAdmission.cs"
DELETE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeDeletionPreparation.cs"
TEST_INVENTORY = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryIncompleteInventoryTests.cs"
TEST_BUDGET = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryCaptureAdmissionTests.cs"
TEST_DELETE = ROOT / "tests/GetDocument.Office.Tests/RecoveryNativeDeletionPreparationTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryOrphanBudgetNativeDeleteSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.budget = BUDGET.read_text(encoding="utf-8")
        cls.deletion = DELETE.read_text(encoding="utf-8")
        cls.inventory_tests = TEST_INVENTORY.read_text(encoding="utf-8")
        cls.budget_tests = TEST_BUDGET.read_text(encoding="utf-8")
        cls.delete_tests = TEST_DELETE.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.capture = cls.source.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        cls.inspect = cls.source.split(
            "internal OfficeRecoveryIncompleteInventory InspectIncompleteWithEnumerator(", 1
        )[1].split("private static bool TryClassifyIncompleteName(", 1)[0]
        cls.classifier = cls.source.split(
            "private static bool TryClassifyIncompleteName(", 1
        )[1].split("public OfficeRecoveryCopy? FindLatest(", 1)[0]

    def test_orphan_classification_requires_complete_name_observation(self):
        for marker in (
            "var observedEntries = new List<string>(maxEntries);",
            "foreach (string entry in enumerateEntries(folder))",
            "if (++visited > maxEntries)",
            "truncated = true;",
            "var observedNames = new HashSet<string>(",
            "foreach (string entry in observedEntries)",
            "OfficeRecoveryIncompleteKind.FinalCopyWithoutObservedManifest",
            '(truncated || observedNames.Contains(name[..32] + ".json"))',
            "tail.Equals(extension, StringComparison.OrdinalIgnoreCase)",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.inspect + self.classifier)
        self.assertIn("FinalCopyWithoutObservedManifest", self.source)
        self.assertLess(self.inspect.index("var observedNames = new HashSet<string>("),
                        self.inspect.index("if (kind == OfficeRecoveryIncompleteKind."
                                           "FinalCopyWithoutObservedManifest"))

    def test_inventory_is_read_only_and_source_bounded(self):
        for forbidden in (
            "File.Delete(", "Directory.Delete(", "File.WriteAll", "File.Move(",
            "File.Replace(", "new FileStream(", "File.ReadAllBytes(",
            "File.ReadAllText(", "Directory.CreateDirectory(",
        ):
            self.assertNotIn(forbidden, self.inspect)
        self.assertIn("SearchOption.TopDirectoryOnly", self.source)
        self.assertIn("if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry)),",
                      self.inspect)
        self.assertIn("OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile",
                      self.inspect)

    def test_capture_blocks_new_exports_when_incomplete_or_truncated(self):
        self.assertIn("RecoveryCaptureAdmission.EnsureMayCapture(", self.capture)
        self.assertIn("InspectIncomplete(source, maxEntries: 1024,", self.capture)
        self.assertLess(self.capture.index("RecoveryCaptureAdmission.EnsureMayCapture("),
                        self.capture.index("Directory.CreateDirectory(folder);"))
        self.assertIn("MaxIncompleteCandidates = 16", self.budget)
        self.assertIn("MaxDirectoryEntriesBeforeCapture = 64", self.budget)
        self.assertIn("inventory.ObservedEntries >= MaxDirectoryEntriesBeforeCapture",
                      self.budget)
        self.assertIn("ObservedEntries: observedEntries.Count", self.inspect)
        self.assertIn("if (inventory.Truncated)", self.budget)
        self.assertIn("inventory.Candidates.Count >= MaxIncompleteCandidates",
                      self.budget)
        for forbidden in ("File.Delete(", "Directory.Delete(", "TryDelete(", "File.Move("):
            self.assertNotIn(forbidden, self.budget)
        self.assertNotIn("TryDelete(temporaryCopy);", self.capture)

    def test_native_preparation_never_calls_delete(self):
        for marker in (
            "internal readonly record struct RecoveryNativeDeleteKey",
            "internal readonly record struct RecoveryNativeDeleteEvidence",
            "IRecoveryNativeDeleteObservationAdapter",
            "RecoveryNativeDeletionPreparationGate",
            "RecoveryNativeDeleteKey.Create(key.SourceFolderHash, key.ArtifactName)",
            "ParentAtOpen != evidence.ParentBeforeDelete",
            "FileAtOpen != evidence.FileBeforeDelete",
            "!evidence.NoFollowEnforced",
            "!evidence.ParentWriteIsolationConfirmed",
            "!evidence.FileHandlePinned",
            "SupportsHandleBoundDeleteObservation => false;",
            "throw new PlatformNotSupportedException(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.deletion)
        self.assertEqual(2, self.deletion.count(
            "SupportsHandleBoundDeleteObservation => false;"))
        for forbidden in ("File.Delete(", "File.Move(", "File.Replace(", "unlinkat(",
                          "FILE_DISPOSITION_INFO_EX("):
            self.assertNotIn(forbidden, self.deletion)
        self.assertNotIn("RecoveryNativeDeletionPreparationGate", self.source)

    def test_test_specs_cover_manifest_race_budget_and_no_delete(self):
        for name in (
            "RenamedFinalCopyWithoutManifestIsListedOnlyAsUnverifiedCandidate",
            "FinalCopyWithEvenMalformedManifestIsNotCalledOrphan",
            "TruncatedEnumerationMustNotClaimFinalCopyHasNoManifest",
            "FinalCopyForOtherFormatOrNonGuidNameIsNeverClassified",
        ):
            self.assertIn(name, self.inventory_tests)
        self.assertEqual(14, self.inventory_tests.count("[TestMethod]"))
        for name in (
            "EmptyAndBelowBudgetInventoryPermitAttempt",
            "UntrustedOrphanCountAtLimitDeniesNewCaptureWithoutDeleting",
            "PartialDirectorySurveyFailsClosedRegardlessOfObservedCount",
            "CaptureAtOrphanBudgetRejectsBeforeCallingExporterAndPreservesBytes",
            "TotalDirectoryBudgetRejectsEvenWhenAllManifestNamesArePresent",
            "ManyMalformedManifestPairsCannotEvadeSourceAdmission",
        ):
            self.assertIn(name, self.budget_tests)
        self.assertEqual(6, self.budget_tests.count("[TestMethod]"))
        self.assertIn("ConsistentFakeEvidenceCreatesAuditObservationButNeverDeletes",
                      self.delete_tests)
        self.assertIn("RealPlatformAdaptersRefuseAllDeletePreparations",
                      self.delete_tests)
        self.assertEqual(6, self.delete_tests.count("[TestMethod]"))

    def test_runner_keeps_platform_gates_manual_and_release_closed(self):
        name = 'CommandStep("recovery-orphan-budget-native-delete-source-tests"'
        self.assertEqual(1, self.runner.count(name))
        self.assertIn('"-p", "test_recovery_orphan_budget_native_delete_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index(name),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bl-final-orphan-windows-ntfs-manual-e2e",
            "gd31j27bl-admission-astra-ext4-manual-e2e",
            "gd31j27bl-smb-concurrent-admission-manual-e2e",
            "gd31j27bl-native-identity-delete-denial-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
