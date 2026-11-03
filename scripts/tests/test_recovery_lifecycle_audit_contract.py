"""GD-31J.27bj: review-only lifecycle invariants (source inspection only).

These assertions do not execute C#, perform a crash test, prove NTFS/SMB
DeleteOnClose semantics, or authorize recovery/WORM integration.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
DIR = ROOT / "src/GetDocument.Office/LibreOffice"
STAGE = DIR / "RecoveryVerifiedStreamStage.cs"
OWNER = DIR / "RecoveryOwnedConsumerOutput.cs"
PUBLISHER = DIR / "RecoveryPrivateCopyPublisher.cs"
STORE = DIR / "OfficeRecoveryStore.cs"
ADAPTERS = DIR / "RecoveryNativeIdentityAdapters.cs"
STAGE_TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryVerifiedStagingTests.cs"
OWNER_TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryOwnedConsumerOutputTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryLifecycleAuditContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.stage = STAGE.read_text(encoding="utf-8")
        cls.owner = OWNER.read_text(encoding="utf-8")
        cls.publisher = PUBLISHER.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.stage_tests = STAGE_TESTS.read_text(encoding="utf-8")
        cls.owner_tests = OWNER_TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_stage_dispose_does_not_close_source_during_active_copy(self):
        copy = self.stage.split("internal async Task<RecoveryStagedCopyReceipt> StageToAsync(", 1)[1]
        disposal = self.stage.split("public void Dispose()", 1)[1]
        for marker in (
            "CancellationTokenSource _lifetimeCancellation",
            "CancellationTokenSource.CreateLinkedTokenSource(",
            "CancellationToken abort = linked.Token;",
            "await source.ReadAsync(",
            "buffer.AsMemory(0, allowed), abort",
            "await staging.FlushAsync(abort)",
            "CloseAndCleanup();",
        ):
            self.assertIn(marker, self.stage)
        self.assertIn("if (previous != 1)", disposal)
        self.assertIn("CloseAndCleanup(); // active copy closes its own handle in finally",
                      disposal)
        self.assertIn("if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;",
                      disposal)
        self.assertLess(copy.index("await staging.WriteAsync("),
                        copy.index("CloseAndCleanup();"))

    def test_stage_rejected_parallel_writer_does_not_own_or_reset_staging(self):
        method = self.stage.split("StageToAsync(", 1)[1].split(
            "private static void TryResetPrivateStaging(", 1)[0]
        self.assertIn("Interlocked.CompareExchange(ref _state, 1, 0)", method)
        self.assertIn("bool privateEmptyStaging = false;", method)
        self.assertLess(method.index("staging.Length != 0 || staging.Position != 0"),
                        method.index("privateEmptyStaging = true;"))
        self.assertIn("if (privateEmptyStaging)", method)
        self.assertNotIn("staging.Dispose()", method)

    def test_consumer_must_drain_unawaited_reads_before_postcheck(self):
        read = self.owner.split("internal async Task<T> ReadVerifiedAsync<T>(", 1)[1].split(
            "private static void RequireRegularPrivateDirectory(", 1)[0]
        self.assertIn("T result = await reader(view, abort);", read)
        self.assertIn("await view.RevokeAndDrainAsync();", read)
        self.assertEqual(2, read.count("await view.RevokeAndDrainAsync();"))
        self.assertLess(read.index("T result = await reader(view, abort);"),
                        read.index("await view.RevokeAndDrainAsync();"))
        self.assertLess(read.index("await view.RevokeAndDrainAsync();"),
                        read.rindex("OfficeRecoveryStore.VerifyOpenedPackage("))
        self.assertIn("finally", read)
        self.assertIn("CloseAndCleanup();", read)

    def test_revocation_waits_for_inflight_and_rejects_overlap(self):
        view = self.owner.split(
            "internal sealed class RevocableReadOnlyStream : Stream", 1)[1]
        for marker in (
            "private readonly object _gate = new();",
            "private bool _active;",
            "if (_active)",
            "_active = true;",
            "private void End()",
            "TaskCompletionSource<bool>? _drain;",
            "internal async Task RevokeAndDrainAsync()",
            "await drain;",
            "_revocationCancellation.Cancel();",
            "Interlocked.Exchange(ref _source, null);",
            "CancellationTokenSource.CreateLinkedTokenSource(",
            "finally { End(); }",
        ):
            self.assertIn(marker, view)
        self.assertNotIn("_source?.Dispose()", view)
        self.assertIn("public override bool CanWrite => false;", view)

    def test_crash_cleanup_remains_nonrecursive_and_not_autoauthorized(self):
        for marker in (
            'Directory.CreateTempSubdirectory("getdoc-consumer-owned-")',
            "FileOptions.Asynchronous | FileOptions.DeleteOnClose",
            "Directory.Delete(directory, recursive: false);",
        ):
            self.assertIn(marker, self.owner)
        self.assertNotIn("File.Delete(", self.owner)
        self.assertIn("File.Move(stagingPath, destination, overwrite: false)", self.publisher)
        self.assertIn("TryDeleteOwnRegularFile(", self.publisher)
        self.assertIn("Directory.Delete(directory, recursive: false);", self.owner)
        self.assertNotIn("Directory.Delete(directory, recursive: true);", self.owner)

    def test_new_synthetic_regressions_are_present_but_not_claimed_run(self):
        self.assertIn("DisposeWhileStageWriteIsPendingKeepsSourceHandleUntilTransferStops",
                      self.stage_tests)
        self.assertIn("ParallelStageAttemptCannotStealOrCloseActiveSourceLease",
                      self.stage_tests)
        self.assertIn("RevocationDrainsUnawaitedPendingReadBeforeOwnerMayCloseHandle",
                      self.owner_tests)
        self.assertIn("ConcurrentViewReadsAreRejectedWhileOneReadIsPending",
                      self.owner_tests)
        self.assertEqual(10, self.stage_tests.count("[TestMethod]"))
        self.assertEqual(11, self.owner_tests.count("[TestMethod]"))

    def test_release_and_native_identity_gates_stay_closed(self):
        self.assertNotIn("RecoveryOwnedConsumerOutput", self.store)
        self.assertNotIn("RecoveryVerifiedStreamStage", self.store)
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))
        self.assertIn('"releaseReady": False', self.runner)
        for gate in (
            "gd31j27bj-lifecycle-windows-ntfs-kill-manual-e2e",
            "gd31j27bj-lifecycle-astra-ext4-sigkill-manual-e2e",
            "gd31j27bj-lifecycle-smb-disconnect-race-manual-e2e",
            "gd31j27bj-orphan-cleanup-authorization-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        step = 'CommandStep("recovery-lifecycle-audit-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn('"-p", "test_recovery_lifecycle_audit_contract.py"', self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))


if __name__ == "__main__":
    unittest.main()
