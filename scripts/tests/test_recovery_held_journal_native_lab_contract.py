"""GD-31J.27ca: source-only checks for held-stream journal lab and Linux dirfsync.

These tests DO NOT execute native Windows/Astra/SMB I/O and DO NOT
authorize persistent production storage, cleanup, restore or publication.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TESTS = ROOT / "tests/GetDocument.Office.Tests"
RUNNER = ROOT / "scripts/office_acceptance.py"


class HeldStreamWriteReadbackContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = (OFFICE / "RecoveryHeldJournalScratchLab.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryHeldJournalScratchLabTests.cs").read_text(encoding="utf-8")
        cls.codec = (OFFICE / "RecoveryCrashJournalCodec.cs").read_text(encoding="utf-8")

    def test_write_on_existing_empty_held_stream_only(self):
        for term in (
            "Stream ownedEmptyScratch", "ownedEmptyScratch.Length != 0",
            "ownedEmptyScratch.Position != 0", "!ownedEmptyScratch.CanSeek",
            "!ownedEmptyScratch.CanWrite", "!ownedEmptyScratch.CanRead",
            "ReferenceEquals(ownedEmptyScratch, Stream.Null)",
            "ownedEmptyScratch.WriteAsync(encodedJournal, cancellationToken)"
        ):
            self.assertIn(term, self.src)

    def test_bounded_canonical_and_chain_check_before_writes(self):
        for term in (
            "RecoveryCrashJournalCodec.DecodeCandidate(bytes)",
            "RecoveryCrashJournalReconciler.Analyze(frames, expectedTransactionId,",
            "if (frames.Length == 0)",
            "cancellationToken.ThrowIfCancellationRequested();"
        ):
            self.assertIn(term, self.src)
        self.assertLess(
            self.src.index("RecoveryCrashJournalReconciler.Analyze(frames, expectedTransactionId,"),
            self.src.index("ownedEmptyScratch.WriteAsync(encodedJournal, cancellationToken)"))
        self.assertIn("MaxEnvelopeBytes = 8192", self.codec)

    def test_real_filedescriptor_flush_but_not_directory_durability(self):
        for term in (
            "ownedEmptyScratch.FlushAsync(cancellationToken)",
            "openedFile.Flush(flushToDisk: true)",
            "ObserveFileIdIfExistingFileStream(ownedEmptyScratch)",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(",
            "DirectoryEntryDurabilityVerified => false;",
            "ContentDurabilityVerified => false;",
        ):
            self.assertIn(term, self.src)

    def test_read_again_from_same_held_handle_without_path(self):
        for term in (
            "ReadOnHeldStreamAsync(", "callerOpenedJournal.Position = 0;",
            "callerOpenedJournal.ReadExactlyAsync(bytes, cancellationToken)",
            "callerOpenedJournal.Length != size",
            "RecoveryCrashJournalCodec.DecodeCandidate(bytes)",
            "RecoveryCrashJournalReconciler.Analyze(",
            "ObserveFileIdIfExistingFileStream(callerOpenedJournal)",
        ):
            self.assertIn(term, self.src)

    def test_failure_never_rolls_back_or_deletes(self):
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Create(", "File.Open(",
            "Directory.Delete(", "Directory.Create", "FileStream(",
            "SetLength(", "File.Write", "unlinkat(", "renameat2(",
        ):
            self.assertNotIn(forbidden, self.src)
        for term in (
            "ProductionWriterEnabled => false;",
            "CanCleanupByPath => false;",
            "CanAuthorizeRecovery => false;",
            "CanAuthorizeCleanup => false;",
            "CanRestore => false;", "CanDelete => false;", "CanPublish => false;",
            "HasTrustedPersistence => false;",
        ):
            self.assertIn(term, self.src)

    def test_fifteen_authored_mstest_cases_including_faults(self):
        self.assertEqual(15, self.tests.count("[TestMethod]"))
        for term in (
            "RealOwnedFixtureFileCanBeReopenedButRemainsUntrusted",
            "InjectedPartialWriteFaultIsNotRolledBackOrDeleted",
            "CancellationAfterPartialWriteLeavesDataForManualReview",
            "TruncatedExistingHeldStreamIsInvalidJournal",
            "OversizedHeldStreamIsRejectedBeforeAllocation",
            "InvalidOrderInValidCanonicalEnvelopeFailsBeforeWrite",
        ):
            self.assertIn(term, self.tests)


class LinuxDirectorySyncLabContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = (OFFICE / "RecoveryHeldLinuxDirectorySyncLab.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryHeldLinuxDirectorySyncLabTests.cs").read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")

    def test_linux_only_held_fd_fsync_without_path(self):
        for term in (
            "SyncExistingDirectoryForLab(", "SafeFileHandle heldDirectory",
            "heldDirectory.DangerousAddRef(ref pinned)",
            "heldDirectory.DangerousGetHandle().ToInt32()",
            "Fsync(heldDirectory.DangerousGetHandle().ToInt32())",
            "heldDirectory.DangerousRelease()",
            "RuntimeInformation.ProcessArchitecture",
            'DllImport("libc", EntryPoint = "fsync"'
        ):
            self.assertIn(term, self.src)

    def test_private_owner_identity_and_uid_check(self):
        for term in (
            "RecoveryPinnedLinuxParentProbe.RequirePrivateOwner(first, uid)",
            "RecoveryPinnedLinuxParentProbe.ReadExistingDirectoryEvidence(heldDirectory)",
            "GetEffectiveUserId() != uid",
            "RecoveryHeldDirectorySyncFinding.ChangedDirectory",
            "RecoveryHeldDirectorySyncFinding.IneligibleDirectory",
        ):
            self.assertIn(term, self.src)

    def test_diagnostic_receipts_never_grant_native_authority(self):
        for term in (
            "ProductionDirectorySyncEnabled => false;",
            "WindowsDirectorySyncImplemented => false;",
            "SmbDurabilityVerified => false;",
            "CanAuthorizeJournalCommit => false;",
            "HasIndependentlyVerifiedDurability => false;",
            "CanAuthorizePublication => false;",
            "CanAuthorizeDeletion => false;",
        ):
            self.assertIn(term, self.src)
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Create(", "FileStream(",
            "Directory.Delete(", "Directory.Create", "unlinkat(", "renameat2(",
        ):
            self.assertNotIn(forbidden, self.src)
        self.assertNotIn("RecoveryHeldLinuxDirectorySyncLab", self.store)

    def test_eleven_authored_mstest_cases(self):
        self.assertEqual(11, self.tests.count("[TestMethod]"))
        for term in (
            "MatchingPrivateParentEvidenceIsOnlyDiagnostic",
            "ChangedFileIdFailsClosed",
            "DifferentMountFailsClosed",
            "GroupWritableDirectoryIsRejected",
            "InvalidSafeHandleIsRefusedBeforeLinuxSyscalls",
            "WindowsAndUnknownPlatformAreNeverClaimedSupported",
        ):
            self.assertIn(term, self.tests)


class CaAcceptanceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")
        cls.gate = (OFFICE / "RecoveryPairMutationGate.cs").read_text(encoding="utf-8")

    def test_registered_offline_and_manual_gates(self):
        step = 'CommandStep("recovery-held-journal-native-lab-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_held_journal_native_lab_contract.py"',
            self.runner)
        for gate in (
            "gd31j27ca-windows-held-file-content-flush-manual-e2e",
            "gd31j27ca-astra-linux-held-dirfd-fsync-manual-e2e",
            "gd31j27ca-windows-ntfs-fileid-reopen-manual-e2e",
            "gd31j27ca-astra-ext4-enospc-eio-interruption-manual-e2e",
            "gd31j27ca-smb-crosshost-name-swap-manual-e2e",
            "gd31j27ca-restart-truncated-journal-readback-manual-e2e",
            "gd31j27ca-windows-directory-durability-qualification-manual-e2e",
            "gd31j27ca-worm-original-untouched-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)

    def test_no_production_wiring_and_previous_delete_denial(self):
        self.assertNotIn("RecoveryHeldJournalScratchLab.", self.store)
        self.assertNotIn("RecoveryHeldLinuxDirectorySyncLab.", self.store)
        self.assertIn("CanDeleteByPath => false;", self.gate)
        self.assertIn("CanPublishByPath => false;", self.gate)


if __name__ == "__main__":
    unittest.main()
