"""GD-31J.27bi — source-only contracts for file-backed consumer-owned output.

These are text checks, NOT a .NET build, MSTest execution, NTFS/Astra/SMB
security evidence, OS-level no-follow guarantee or permission to restore/WORM.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OWNER = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryOwnedConsumerOutput.cs"
PUBLISHER = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryPrivateCopyPublisher.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryOwnedConsumerOutputTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryOwnedConsumerSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.owner = OWNER.read_text(encoding="utf-8")
        cls.publisher = PUBLISHER.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.create = cls.owner.split(
            "internal static async Task<RecoveryOwnedConsumerOutput> CreateAsync(", 1
        )[1].split("internal async Task<T> ReadVerifiedAsync<T>(", 1)[0]
        cls.read = cls.owner.split(
            "internal async Task<T> ReadVerifiedAsync<T>(", 1
        )[1].split("private static void RequireRegularPrivateDirectory(", 1)[0]
        cls.view = cls.owner.split(
            "private sealed class RevocableReadOnlyStream : Stream", 1
        )[1]

    def test_owned_buffer_is_new_private_file_not_memory_or_public_path(self):
        self.assertIn('Directory.CreateTempSubdirectory("getdoc-consumer-owned-")',
                      self.create)
        self.assertIn('string filename = Guid.NewGuid().ToString("N") + ".consumer";',
                      self.create)
        self.assertIn("new FileStream(path, FileMode.CreateNew,", self.create)
        self.assertIn("FileAccess.ReadWrite, FileShare.None", self.create)
        self.assertIn("FileOptions.Asynchronous | FileOptions.DeleteOnClose", self.create)
        self.assertNotIn("new MemoryStream(", self.owner)
        self.assertNotIn("File.Delete(", self.owner)
        self.assertNotIn("public string FilePath", self.owner)
        self.assertNotIn("internal string FilePath", self.owner)

    def test_previous_private_publication_consumed_and_not_reopened_by_path(self):
        self.assertIn("using (publication)", self.create)
        self.assertIn("await publication.ConsumeVerifiedToAsync(held, cancellationToken)",
                      self.create)
        self.assertIn("held.Flush(flushToDisk: true);", self.create)
        self.assertIn("OfficeRecoveryStore.VerifyOpenedPackage(", self.create)
        self.assertIn("held, receipt.Extension, receipt.Sha256", self.create)
        self.assertIn("held = null; // ownership moved", self.create)
        self.assertNotIn("File.OpenRead(", self.owner)
        self.assertNotIn("new FileStream(publication.FilePath", self.owner)
        self.assertNotIn("Path.GetFullPath(publication.FilePath", self.owner)

    def test_read_verifies_held_stream_before_and_after_callback(self):
        self.assertIn("FileStream source = _heldBuffer", self.read)
        self.assertIn("Func<Stream, CancellationToken, Task<T>> reader", self.read)
        self.assertIn("OfficeRecoveryStore.VerifyOpenedPackage(", self.read)
        self.assertEqual(2, self.read.count("OfficeRecoveryStore.VerifyOpenedPackage("))
        self.assertLess(self.read.index("VerifyOpenedPackage("),
                        self.read.index("T result = await reader(view, abort);"))
        self.assertLess(self.read.index("T result = await reader(view, abort);"),
                        self.read.rindex("VerifyOpenedPackage("))
        self.assertIn("source, Receipt.Extension, Receipt.Sha256", self.read)
        self.assertIn("return result;", self.read)
        self.assertNotIn("FilePath", self.read)
        self.assertNotIn("new FileStream(", self.read)

    def test_readonly_view_is_revoked_without_closing_held_handle_early(self):
        self.assertIn("new RevocableReadOnlyStream(source, abort)", self.read)
        self.assertIn("using var view =", self.read)
        self.assertIn("public override bool CanWrite => false;", self.view)
        self.assertIn("public override void Write(byte[] buffer, int offset, int count)",
                      self.view)
        self.assertIn("throw new NotSupportedException();", self.view)
        self.assertIn("Interlocked.Exchange(ref _source, null);", self.view)
        self.assertIn("throw new ObjectDisposedException(", self.view)
        self.assertIn("CancellationTokenSource.CreateLinkedTokenSource(", self.view)
        self.assertNotIn("public FileStream", self.view)
        self.assertNotIn("_source?.Dispose()", self.view)

    def test_single_use_and_disposal_cancellation_defer_handle_cleanup(self):
        self.assertIn("Interlocked.CompareExchange(ref _state, 1, 0)", self.read)
        self.assertIn("Interlocked.CompareExchange(ref _state, 2, 1)", self.read)
        self.assertIn("CancellationTokenSource.CreateLinkedTokenSource(", self.read)
        self.assertIn("_lifetimeCancellation.Cancel();", self.owner)
        self.assertIn("if (previous != 1)", self.owner)
        self.assertIn("CloseAndCleanup(); // callback owns active handle until finally",
                      self.owner)
        self.assertIn("Interlocked.Exchange(ref _cleanupStarted, 1)", self.owner)
        self.assertIn("held?.Dispose(); // OS deletes held file", self.owner)
        self.assertIn("Directory.Delete(directory, recursive: false);", self.owner)
        self.assertNotIn("Directory.Delete(directory, recursive: true);", self.owner)

    def test_failed_acceptance_fails_closed_and_never_publishes_user_files(self):
        self.assertIn("cancellationToken.ThrowIfCancellationRequested();", self.create)
        self.assertIn("RequireRegularPrivateDirectory(root);", self.create)
        self.assertIn("held?.Dispose(); // OS-backed DeleteOnClose", self.create)
        self.assertIn("TryRemoveEmptyDirectory(root);", self.create)
        for forbidden in ("File.Move(", "File.Replace(", "File.WriteAllBytes(",
                          "RecoveryPrivateCopyPublisher.PublishAsync(", "Worm",
                          "SaveAs", "FilePath ="):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.owner)
        self.assertNotIn("RecoveryOwnedConsumerOutput", self.store)
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))

    def test_synthetic_mstest_covers_readonly_races_and_failing_receipt(self):
        names = (
            "VerifiedFileBackedConsumerGetsReadOnlyBytesWithNoPath",
            "ConsumerViewIsRevokedImmediatelyAfterCallback",
            "ASecondReaderCannotOverlapFirstOrReuseConsumedOutput",
            "DisposeDuringCallbackCancelsAndWaitsForCallbackExitToCleanup",
            "ConsumerCallbackExceptionRevokesViewAndCleansOwnedFile",
            "CancelledAcceptanceDoesNotCreatePrivateBufferOrTouchSource",
            "WrongPublicationDigestFailsClosedBeforeOwnerIsReturned",
            "UnconsumedOwnerDisposeClosesBufferAndForbidsReads",
            "RealDisabledNativeAdapterCannotCreateOwnedOutput",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(9, self.tests.count("[TestMethod]"))
        self.assertIn("new FakeAdapter(", self.tests)
        self.assertIn("TaskCompletionSource<bool>", self.tests)

    def test_runner_manual_platform_gates_still_block_release(self):
        name = "recovery-owned-consumer-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
        self.assertIn('"-p", "test_recovery_owned_consumer_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bi-owned-consumer-windows-ntfs-manual-e2e",
            "gd31j27bi-owned-consumer-astra-ext4-manual-e2e",
            "gd31j27bi-owned-consumer-smb-delete-on-close-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
