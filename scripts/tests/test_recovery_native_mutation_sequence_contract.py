"""GD-31J.27bx: source-only contract for inert native mutation planning.

Does not compile C#, run native APIs, perform filesystem mutations, qualify
Windows/Astra/SMB, approve crash recovery or grant release permissions.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
PLANNER = OFFICE / "RecoveryNativeMutationPlanner.cs"
PAIR_GATE = OFFICE / "RecoveryPairMutationGate.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryNativeMutationSequenceTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class NativeMutationSequenceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.planner = PLANNER.read_text(encoding="utf-8")
        cls.pair_gate = PAIR_GATE.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_no_native_backend_and_no_write_delete_operation(self):
        self.assertIn("NativeBackendEnabled => false;", self.planner)
        for token in ("CanAuthorizeNativeMutation => false;",
                      "CanAuthorizeCleanup => false;", "ReleaseReady => false;"):
            self.assertIn(token, self.planner)
        for forbidden in ("File.Delete(", "File.Move(", "File.Write",
                          "FileStream(", "unlinkat(", "renameat2(",
                          "SetFileInformationByHandle(", "DllImport(",
                          "LibraryImport(", "Directory.Delete("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.planner)

    def test_start_requires_nonempty_id_and_synthetic_consistent_pair(self):
        for token in ("Enum.IsDefined(kind)", "transactionId == Guid.Empty",
                      "RecoveryPairMutationGate.Evaluate(evidence)",
                      "RecoveryPairMutationDecision.NeedsNativeBoundOperation",
                      "RecoveryNativeMutationPhase.Initialized"):
            self.assertIn(token, self.planner)

    def test_publish_order_needs_no_replace_and_directory_sync(self):
        for token in ("FlushCopyContent", "RevalidateCopyName",
                      "PublishCopyNoReplace", "CopyNameChanged",
                      "SyncParentDirectory", "CopyDirectorySyncedClaimed",
                      "FlushManifestContent", "RevalidateManifestName",
                      "PublishManifestNoReplace", "ManifestNameChanged",
                      "SequenceObserved"):
            self.assertIn(token, self.planner)

    def test_delete_order_copy_before_manifest_and_no_swap(self):
        self.assertIn("UnlinkCopyEntry", self.planner)
        self.assertIn("UnlinkManifestEntry", self.planner)
        self.assertIn("RecoveryNativeMutationKind.DeletePair", self.planner)
        self.assertIn("RecoveryNativeMutationPhase.CopyDirectorySyncedClaimed", self.planner)
        self.assertIn("RecoveryNativeMutationPhase.ManifestNameRevalidated", self.planner)

    def test_cancel_crash_and_lock_loss_are_fail_closed(self):
        for token in ("CancelRequested", "ProcessInterrupted",
                      "ExclusiveLockLost", "HasClaimedMutation(plan.Phase)",
                      "RequiresManualReview", "SuspendedWithoutMutation",
                      "if (plan.Phase is RecoveryNativeMutationPhase.SequenceObserved"):
            self.assertIn(token, self.planner)

    def test_zero_runtime_wiring_and_legacy_denial_still_intact(self):
        self.assertNotIn("RecoveryNativeMutationPlanner.", self.store)
        self.assertNotIn("RecoveryNativeMutationPlanner.", self.pair_gate)
        self.assertIn("CanDeleteByPath => false;", self.pair_gate)
        self.assertIn("CanPublishByPath => false;", self.pair_gate)
        self.assertIn("// No deletion callback is invoked.", self.store)

    def test_fourteen_synthetic_mstest_methods_present(self):
        self.assertEqual(14, self.tests.count("[TestMethod]"))
        for token in ("PublishRequiresCopyThenManifestAndTwoDirectorySyncClaims",
                      "CannotDeleteManifestBeforeCopyAndDirectorySync",
                      "CancellationAfterCopyPublishRequestsManualReviewNotRollback",
                      "CrashAfterCopyUnlinkCannotAuthorizeDeletingManifest",
                      "LockLossFailsClosedEvenBeforeFirstMutation",
                      "CannotReplayCompletedClaimsToProduceAnAuthorization"):
            self.assertIn(token, self.tests)

    def test_manual_acceptance_gates_and_release_off(self):
        step = 'CommandStep("recovery-native-mutation-sequence-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_native_mutation_sequence_contract.py"',
            self.runner)
        for gate in (
            "gd31j27bx-windows-ntfs-no-replace-directory-fsync-manual-e2e",
            "gd31j27bx-astra-renameat2-unlinkat-dirfsync-manual-e2e",
            "gd31j27bx-smb-multi-host-lock-crash-manual-e2e",
            "gd31j27bx-transaction-restart-reconcile-manual-e2e",
            "gd31j27bx-eurooffice-native-saveas-integration-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
