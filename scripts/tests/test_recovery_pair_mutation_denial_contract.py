"""GD-31J.27bv: fail-closed deletion and held-identity source contracts.

Source-only predicates. Does NOT compile .NET or run NTFS/Astra/SMB E2E.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
GATE = OFFICE / "RecoveryPairMutationGate.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
TEST = ROOT / "tests/GetDocument.Office.Tests/RecoveryPairMutationSafetyTests.cs"
LEGACY_TEST = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryStoreTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryPairMutationSafetyContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.test = TEST.read_text(encoding="utf-8")
        cls.legacy = LEGACY_TEST.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_both_default_path_mutation_permissions_are_false(self):
        self.assertIn("internal static bool CanDeleteByPath => false;", self.gate)
        self.assertIn("internal static bool CanPublishByPath => false;", self.gate)
        self.assertIn("return false;", self.gate)
        self.assertNotIn("File.Delete(", self.gate)
        self.assertNotIn("File.Move(", self.gate)
        self.assertNotIn("SetFileInformationByHandle(", self.gate)

    def test_capture_identity_reread_is_only_diagnostic(self):
        for token in (
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldParent)",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldCopy)",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(heldManifest)",
            "CopyAtRecheck", "ManifestAtRecheck", "ReplacedOrChanged",
            "AliasedPair", "CrossVolume", "MissingExclusiveOwnership",
            "NeedsNativeBoundOperation", "NamesOpenedRelativeToParent: false",
            "WriterIsolationConfirmed: false",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.gate)
        self.assertNotIn("OpenFile(", self.gate)
        self.assertNotIn("File.Open(", self.gate)

    def test_discard_declines_unbound_path_cleanup(self):
        fragment = self.store.split("public void Discard(string sourcePath)", 1)[1]
        fragment = fragment.split("ReadValidCopyWithPostHashMutation", 1)[0]
        self.assertIn("_ = RecoveryPairMutationGate.CanDeleteByPath;", fragment)
        self.assertNotIn("DeleteVerifiedPair(", fragment)
        self.assertNotIn("Directory.EnumerateFiles(", fragment)

    def test_prune_declines_unbound_path_cleanup(self):
        fragment = self.store.split("private void Prune(string source)", 1)[1]
        fragment = fragment.split("private static void RejectLinkedDirectories", 1)[0]
        self.assertIn("_ = RecoveryPairMutationGate.CanDeleteByPath;", fragment)
        self.assertNotIn("DeleteVerifiedPair(", fragment)
        self.assertNotIn("Directory.EnumerateFiles(", fragment)

    def test_legacy_delete_paths_do_not_reach_file_delete(self):
        self.assertNotIn("File.Delete(", self.store)
        self.assertNotIn("TryDeleteVerifiedPart(", self.store)
        self.assertEqual(1, self.store.count("DeleteVerifiedPair("),
            "Test-only pair ordering helper must have no production callers")
        self.assertIn("if (deleteFile is null) return;", self.store)
        self.assertNotIn("RecoveryPairMutationGate.CanAuthorizeNativeMutation(", self.store)

    def test_capture_pressure_is_bounded_without_deleting_existing_copies(self):
        self.assertIn("RequireCaptureBudget(", self.gate)
        self.assertIn("maxManifests = 32", self.gate)
        self.assertIn("if (++observed >= maxManifests)", self.gate)
        self.assertIn("Directory.EnumerateFiles(folder, \"*.json\", SearchOption.TopDirectoryOnly)",
                      self.store)
        self.assertIn("RecoveryPairMutationGate.RequireCaptureBudget(", self.store)
        self.assertLess(self.store.index("RecoveryPairMutationGate.RequireCaptureBudget("),
                        self.store.index("await exportCopy(temporaryCopy);"))

    def test_existing_user_recovery_contract_tests_now_preserve_copy(self):
        for name in ("DiscardAfterConfirmedSavePreservesVerifiedCopiesWithoutNativeDelete",
                     "UnverifiedPathPruningPreservesFiveSnapshotsPendingNativeCleanup"):
            self.assertIn(name, self.legacy)
        self.assertNotIn("DiscardAfterConfirmedSaveRemovesRecoveryButNotSource", self.legacy)
        self.assertNotIn("RetainsOnlyThreeMostRecentValidatedCopies", self.legacy)

    def test_mstest_rejects_all_path_based_deletion(self):
        self.assertEqual(12, self.test.count("[TestMethod]"))
        for name in (
            "PathBasedDeletionIsAlwaysDenied", "MissingNativeIdentityIsRejected",
            "CopyOrManifestSwapAcrossCheckpointsIsRejected",
            "EvenPlausibleHeldIdsAreNotWriteOrDeleteAuthority",
            "DefaultLegacyCleanupMustNeverDeleteAnUnownedNamedFile",
            "DisabledPruningStopsAdmissionAtBoundedCommittedManifestLimit",
            "CaptureBudgetRejectsUnknownOrInvalidThresholdAndDoesNotHideEnumerationErrors",
        ):
            self.assertIn(name, self.test)

    def test_registered_offline_runner_and_manual_gates(self):
        name = 'CommandStep("recovery-pair-mutation-denial-source-tests"'
        self.assertEqual(1, self.runner.count(name))
        self.assertLess(self.runner.index(name),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn('"-p", "test_recovery_pair_mutation_denial_contract.py"', self.runner)
        for gate in (
            "gd31j27bv-windows-ntfs-delete-denial-manual-e2e",
            "gd31j27bv-astra-ext4-delete-denial-manual-e2e",
            "gd31j27bv-smb-concurrent-replace-denial-manual-e2e",
            "gd31j27bv-retention-disk-budget-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
