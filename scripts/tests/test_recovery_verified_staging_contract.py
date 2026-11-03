"""GD-31J.27bf source-only guard for descriptor-held recovery staging.

This is a TEXT inspection only: not a .NET build, MSTest execution, real
no-follow/openat2 validation, stream stress test, or restore authorization.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
STAGER = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryVerifiedStreamStage.cs"
GATE = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityContracts.cs"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryVerifiedStagingTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryVerifiedStagingSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.stage = STAGER.read_text(encoding="utf-8")
        cls.gate = GATE.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.transfer = cls.stage.split(
            "internal async Task<RecoveryStagedCopyReceipt> StageToAsync(", 1
        )[1].split("private static void TryResetPrivateStaging(", 1)[0]

    def test_factory_must_pass_native_gate_before_owning_handle(self):
        self.assertIn("RecoveryNativeIdentityGate.OpenVerifiedRead(adapter, key)", self.stage)
        self.assertIn("return new RecoveryVerifiedStreamStage(lease, key);", self.stage)
        self.assertIn("private RecoveryVerifiedStreamStage(", self.stage)
        self.assertNotIn("new WindowsRecoveryNativeIdentityAdapter(", self.stage)
        self.assertNotIn("new LinuxRecoveryNativeIdentityAdapter(", self.stage)
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))

    def test_no_path_reopening_or_document_mutations_in_stage(self):
        for forbidden in (
            "File.Open(", "File.ReadAllBytes(", "File.WriteAllBytes(", "File.Move(",
            "File.Delete(", "Directory.CreateDirectory(", "Path.Combine(",
            "new FileStream(", "ZipFile.OpenRead(", "CaptureAsync(", "Discard(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.stage)
        self.assertIn("Stream source = _lease?.Stream", self.transfer)
        self.assertIn("await source.ReadAsync(", self.transfer)
        self.assertIn("await staging.WriteAsync(", self.transfer)
        self.assertIn("OfficeRecoveryStore.VerifyOpenedPackage(", self.transfer)

    def test_stage_is_empty_before_any_write_and_cleanup_is_authorized_only_afterward(self):
        self.assertIn("staging.Length != 0 || staging.Position != 0", self.transfer)
        self.assertIn("bool privateEmptyStaging = false;", self.transfer)
        self.assertIn("privateEmptyStaging = true;", self.transfer)
        self.assertIn("if (privateEmptyStaging)", self.transfer)
        self.assertIn("TryResetPrivateStaging(staging);", self.transfer)
        self.assertLess(self.transfer.index("staging.Length != 0"),
                        self.transfer.index("privateEmptyStaging = true;"))
        self.assertLess(self.transfer.index("privateEmptyStaging = true;"),
                        self.transfer.index("await staging.WriteAsync("))
        self.assertNotIn("staging.Dispose();", self.stage)

    def test_hash_represents_bytes_written_not_cached_source_metadata(self):
        for marker in (
            "IncrementalHash.CreateHash(HashAlgorithmName.SHA256)",
            "hasher.AppendData(buffer, 0, read);",
            "string digest = Convert.ToHexString(hasher.GetHashAndReset());",
            "string.Equals(digest, expectedSha256,",
            "staging.Position = 0;",
            "OfficeRecoveryStore.VerifyOpenedPackage(",
            "staging, extension, digest",
            "return new RecoveryStagedCopyReceipt(copied, digest, extension);",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.transfer)
        self.assertLess(self.transfer.index("await staging.WriteAsync("),
                        self.transfer.index("hasher.AppendData("))
        self.assertLess(self.transfer.index("hasher.GetHashAndReset()"),
                        self.transfer.index("VerifyOpenedPackage("))
        self.assertLess(self.transfer.index("VerifyOpenedPackage("),
                        self.transfer.index("return new RecoveryStagedCopyReceipt("))

    def test_bounded_transfer_and_cancel_are_checked_between_chunks(self):
        for marker in (
            "private const long MaxCopyBytes = 128L * 1024 * 1024;",
            "private const int BufferBytes = 64 * 1024;",
            "ArrayPool<byte>.Shared.Rent(BufferBytes)",
            "MaxCopyBytes - copied + 1",
            "if (copied > MaxCopyBytes)",
            "await staging.FlushAsync(abort);",
            "abort.ThrowIfCancellationRequested();",
            "ArrayPool<byte>.Shared.Return(buffer, clearArray: true);",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.stage)

    def test_source_lease_is_closed_on_all_terminal_paths(self):
        self.assertIn("Interlocked.CompareExchange(ref _state, 1, 0)", self.transfer)
        self.assertIn("Interlocked.CompareExchange(ref _state, 2, 1)", self.transfer)
        self.assertIn("Interlocked.Exchange(ref _lease, null)?.Dispose();", self.stage)
        self.assertIn("public ValueTask DisposeAsync()", self.stage)
        self.assertIn("Interlocked.Exchange(ref _state, 3);", self.stage)
        self.assertIn("throw new ObjectDisposedException(", self.transfer)
        self.assertIn("throw new InvalidOperationException(", self.transfer)
        self.assertNotIn("FilePath", self.transfer)

    def test_csharp_fixtures_cover_success_hash_zip_cancel_and_ownership(self):
        names = (
            "ValidCopyIsStagedFromVerifiedLeaseAndSourceHandleIsReleased",
            "WrongDigestCleansPrivateStagingAndClosesSourceHandle",
            "CorrectDigestOfInvalidZipIsNotARecoveryReceipt",
            "MidWriteCancellationReleasesLeaseAndInvalidatesStaging",
            "PreexistingStagingBytesAreNeverErasedOrOverwritten",
            "AVerifiedSessionIsSingleUseEvenAfterSuccessfulTransfer",
            "DisposingUnconsumedSessionClosesSourceWithoutTouchingStage",
            "DisabledRealNativeAdapterCannotCreateAStagingSession",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(10, self.tests.count("[TestMethod]"))
        self.assertIn("new FakeAdapter(", self.tests)
        self.assertIn("File.ReadAllBytes(fixture.FilePath)", self.tests)

    def test_production_is_not_wired_and_os_evidence_remains_not_run(self):
        for forbidden in (
            "RecoveryVerifiedStreamStage.Open(", "RecoveryNativeIdentityGate.OpenVerifiedRead(",
            "RecoveryNativeReadLease",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.store)
        name = "recovery-verified-staging-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
        self.assertIn('"-p", "test_recovery_verified_staging_contract.py"', self.runner)
        self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for manual in (
            "gd31j27bf-verified-staging-windows-ntfs-manual-e2e",
            "gd31j27bf-verified-staging-astra-ext4-manual-e2e",
            "gd31j27bf-verified-staging-smb-cancellation-manual-e2e",
        ):
            self.assertIn(manual, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
