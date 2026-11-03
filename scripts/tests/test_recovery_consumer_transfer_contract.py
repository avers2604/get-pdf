"""GD-31J.27bh: source-only contracts for owned-handle transfer to consumer.

Text comparisons are NOT a C# build, MSTest execution, native no-follow
evidence, NTFS/Astra/SMB platform test, or authorization to restore/WORM.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
PUBLISHER = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryPrivateCopyPublisher.cs"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryPrivatePublicationTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryConsumerTransferSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.publisher = PUBLISHER.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.owner = cls.publisher.split(
            "internal sealed class RecoveryPrivatePublication : IDisposable", 1
        )[1].split("/// GD-31J.27bg", 1)[0]
        cls.transfer = cls.owner.split(
            "internal async Task<RecoveryStagedCopyReceipt> ConsumeVerifiedToAsync(", 1
        )[1].split("private static void TryClearConsumerDestination(", 1)[0]
        cls.cleanup = cls.publisher.split(
            "internal static void TryDeleteOwnRegularFile(", 1
        )[1].split("internal static void TryRemoveEmptyPrivateDirectory(", 1)[0]

    def test_consumer_uses_held_handle_and_never_reopens_original_name(self):
        self.assertIn("FileStream source = _heldStream", self.transfer)
        self.assertIn("source.Position = 0;", self.transfer)
        self.assertIn("await source.ReadAsync(", self.transfer)
        self.assertIn("await destination.WriteAsync(", self.transfer)
        for forbidden in (
            "File.Open(", "File.ReadAllBytes(", "new FileStream(",
            "File.Move(", "File.Delete(", "Path.Combine(", "FilePath",
            "OfficeRecoveryStore.CaptureAsync(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.transfer)

    def test_source_is_verified_before_destination_first_write(self):
        for expected in (
            "source.Length != Receipt.Length",
            "source.Length > MaxCopyBytes",
            "OfficeRecoveryStore.VerifyOpenedPackage(",
            "source, Receipt.Extension, Receipt.Sha256",
            "abort.ThrowIfCancellationRequested();",
        ):
            self.assertIn(expected, self.transfer)
        self.assertLess(
            self.transfer.index("VerifyOpenedPackage("),
            self.transfer.index("await destination.WriteAsync("))

    def test_transferred_bytes_and_consumer_output_are_independently_verified(self):
        for expected in (
            "IncrementalHash.CreateHash(",
            "hasher.AppendData(buffer, 0, bytes);",
            "transferred != Receipt.Length",
            "string digest = Convert.ToHexString(hasher.GetHashAndReset());",
            "string.Equals(digest, Receipt.Sha256,",
            "destination.Length != Receipt.Length",
            "destination, Receipt.Extension, digest",
            "return new RecoveryStagedCopyReceipt(",
        ):
            with self.subTest(expected=expected):
                self.assertIn(expected, self.transfer)
        self.assertLess(
            self.transfer.index("await destination.WriteAsync("),
            self.transfer.index("hasher.AppendData("))
        self.assertLess(
            self.transfer.index("hasher.GetHashAndReset()"),
            self.transfer.rindex("VerifyOpenedPackage("))
        self.assertLess(
            self.transfer.rindex("VerifyOpenedPackage("),
            self.transfer.index("return new RecoveryStagedCopyReceipt("))

    def test_consumer_is_single_use_and_dispose_defers_cleanup_during_transfer(self):
        for expected in (
            "Interlocked.CompareExchange(ref _deliveryState, 1, 0)",
            "CancellationTokenSource.CreateLinkedTokenSource(",
            "Interlocked.CompareExchange(ref _deliveryState, 2, 1)",
            "Interlocked.Exchange(ref _deliveryState, 3)",
            "_lifetimeCancellation.Cancel();",
            "if (previous != 1)",
            "CloseAndCleanup(); // active transfer closes itself in finally",
            "Interlocked.Exchange(ref _cleanupStarted, 1)",
        ):
            with self.subTest(expected=expected):
                self.assertIn(expected, self.owner)
        self.assertIn("CloseAndCleanup();", self.transfer)
        self.assertIn("Interlocked.Exchange(ref _heldStream, null)", self.owner)
        self.assertIn("handle?.Dispose();", self.owner)

    def test_nonempty_consumer_is_preserved_and_partial_output_reset_only_after_validation(self):
        self.assertIn("destination.Length != 0 ||", self.transfer)
        self.assertIn("destination.Position != 0", self.transfer)
        self.assertIn("ownedEmptyDestination = true;", self.transfer)
        self.assertLess(self.transfer.index("destination.Length != 0"),
                        self.transfer.index("ownedEmptyDestination = true;"))
        self.assertIn("if (ownedEmptyDestination)", self.transfer)
        self.assertIn("TryClearConsumerDestination(destination);", self.transfer)
        self.assertIn("destination.SetLength(0);", self.owner)
        self.assertNotIn("destination.Dispose();", self.owner)

    def test_path_cleanup_revalidates_receipt_and_avoids_foreign_file(self):
        self.assertIn("RecoveryStagedCopyReceipt? expected = null", self.cleanup)
        self.assertIn("if (info.Length != expected.Length) return;", self.cleanup)
        self.assertIn("OfficeRecoveryStore.VerifyOpenedPackage(", self.cleanup)
        self.assertIn("check, expected.Extension, expected.Sha256", self.cleanup)
        self.assertIn("info.LinkTarget is not null", self.cleanup)
        self.assertIn("File.Delete(filePath);", self.cleanup)
        self.assertLess(self.cleanup.index("VerifyOpenedPackage("),
                        self.cleanup.index("File.Delete(filePath);"))
        self.assertIn("_privateDirectory, FilePath, Receipt", self.owner)
        self.assertIn("Directory.Delete(directory, recursive: false);", self.publisher)

    def test_eight_new_csharp_cases_cover_lifecycle_race_and_path_swap(self):
        names = (
            "ConsumerCopiesVerifiedBytesFromHeldHandleAndReleasesPublication",
            "SecondConsumerIsRejectedWhileFirstTransferIsInProgress",
            "DisposalDuringTransferCancelsWithoutEarlyCleanup",
            "CancellationImmediatelyAfterWriteInvalidatesTarget",
            "NonemptyConsumerTargetIsNeverOverwrittenOrErased",
            "CorruptedConsumerBytesFailIndependentShaZipAndClearTarget",
            "VerifiedPublicationCannotBeConsumedTwice",
            "SwappedPathDoesNotChangeHeldSourceBytesOrDeleteForeignReplacement",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(15, self.tests.count("[TestMethod]"))
        self.assertIn("TaskCompletionSource<bool>", self.tests)
        self.assertIn("FOREIGN_AFTER_RENAME", self.tests)

    def test_native_adapters_stay_disabled_and_manual_gate_blocks_release(self):
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))
        self.assertNotIn("RecoveryPrivatePublication", self.store)
        step = "recovery-consumer-transfer-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + step + '"'))
        self.assertIn('"-p", "test_recovery_consumer_transfer_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index('CommandStep("' + step + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bh-consumer-windows-ntfs-manual-e2e",
            "gd31j27bh-consumer-astra-ext4-manual-e2e",
            "gd31j27bh-consumer-smb-race-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
