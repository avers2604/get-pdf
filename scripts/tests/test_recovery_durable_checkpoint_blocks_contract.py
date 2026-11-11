"""GD-31J.27bz: source-only contracts for three independent recovery blocks.

No native Windows/Astra/SMB fsync, .NET compilation, HMAC key attestation,
durable journal, file mutation, or WORM authorization is performed here.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TESTDIR = ROOT / "tests/GetDocument.Office.Tests"
RUNNER = ROOT / "scripts/office_acceptance.py"


class DurableCheckpointSequenceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = (OFFICE / "RecoveryDurableCheckpointProtocol.cs").read_text(encoding="utf-8")
        cls.tests = (TESTDIR / "RecoveryDurableCheckpointProtocolTests.cs").read_text(encoding="utf-8")

    def test_blocks_real_writes_and_release(self):
        for signal in (
            "NativeStoreEnabled => false;", "CanAuthorizeMutation => false;",
            "CanAuthorizeCleanup => false;", "ReleaseReady => false;",
            "HasIndependentlyVerifiedDurability => false;"
        ):
            self.assertIn(signal, self.code)
        for forbidden in ("File.Delete(", "File.Move(", "File.Write", "FileStream(",
                          "Directory.Create", "Directory.Delete(", "DllImport(",
                          "LibraryImport(", "unlinkat(", "renameat2("):
            self.assertNotIn(forbidden, self.code)

    def test_requires_file_directory_and_anchor_sync_in_order(self):
        for label in (
            "JournalFileFlushClaimed", "JournalDirectorySyncClaimed",
            "AnchorFileFlushClaimed", "AnchorDirectorySyncClaimed",
            "RestartReadbackClaimed", "ExpectedStepCount(plan.Phase)",
            "RecoveryDurableCheckpointPhase.ReadbackClaimed"
        ):
            self.assertIn(label, self.code)

    def test_bounded_generation_and_journal_head(self):
        for label in (
            "priorGeneration == ulong.MaxValue",
            "nextGeneration != priorGeneration + 1",
            "RecoveryCrashJournalReconciler.MaxFrames",
            "IsCanonicalSha256(journalHeadSha256)",
            "Guid.Empty"
        ):
            self.assertIn(label, self.code)

    def test_crash_cancel_lock_loss_and_media_error_denied(self):
        for label in (
            "CancelRequested", "ProcessInterrupted", "LockLost", "MediaError",
            "SuspendedWithoutWriteClaim", "RequiresManualReview"
        ):
            self.assertIn(label, self.code)

    def test_twelve_checkpoint_mstest_authored(self):
        self.assertEqual(12, self.tests.count("[TestMethod]"))
        self.assertIn("PowerLossAfterAnchorFileFlushRequiresReview", self.tests)
        self.assertIn("RejectsReplayAndForgedStepCounter", self.tests)


class ProtectedAnchorContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = (OFFICE / "RecoveryProtectedAnchorProtocol.cs").read_text(encoding="utf-8")
        cls.tests = (TESTDIR / "RecoveryProtectedAnchorCandidateTests.cs").read_text(encoding="utf-8")

    def test_keyed_mac_and_constant_time_compare(self):
        for token in (
            "HMACSHA256.HashData(suppliedKey, message)",
            "CryptographicOperations.FixedTimeEquals(expected, observed)",
            "CryptographicOperations.ZeroMemory(expected)",
            "CryptographicOperations.ZeroMemory(observed)",
            "CryptographicOperations.ZeroMemory(mac)"
        ):
            self.assertIn(token, self.code)

    def test_canonical_bounded_key_and_generation(self):
        for token in (
            "FormatVersion = 1",
            "suppliedKey.Length is < 32 or > 64",
            "generation == 0",
            "transactionId == Guid.Empty",
            "string.Equals(candidate.JournalHeadSha256, expectedJournalHeadSha256",
            "candidate.Generation != expectedGeneration"
        ):
            self.assertIn(token, self.code)

    def test_previous_tag_and_rollback_are_explicit(self):
        for token in (
            "PreviousTagMismatch", "GenerationMismatch", "TagMismatch",
            "JournalHeadMismatch", "TransactionMismatch",
            "GET-BZ1|"
        ):
            self.assertIn(token, self.code)

    def test_hmac_does_not_upgrade_untrusted_key(self):
        for token in (
            "NativeTrustStoreConnected => false;",
            "HasIndependentlyTrustedKey => false;",
            "CanAuthorizeRecovery => false;",
            "CanAuthorizeDeletion => false;",
            "CanAuthorizePublication => false;"
        ):
            self.assertIn(token, self.code)
        for forbidden in ("File.Open(", "FileStream(", "File.Delete(",
                          "File.Move(", "DllImport(", "Registry."):
            self.assertNotIn(forbidden, self.code)

    def test_twelve_anchor_mstest_authored(self):
        self.assertEqual(12, self.tests.count("[TestMethod]"))
        self.assertIn("TamperingCandidateFieldWithoutRekeyingFailsMac", self.tests)
        self.assertIn("ReplayedOldGenerationIsRefused", self.tests)


class RestartCheckpointContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = (OFFICE / "RecoveryRestartCheckpointReview.cs").read_text(encoding="utf-8")
        cls.tests = (TESTDIR / "RecoveryRestartCheckpointReviewTests.cs").read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")

    def test_replays_anchor_codec_journal_and_file_ids(self):
        for token in (
            "RecoveryProtectedAnchorProtocol.AssessCandidate(",
            "RecoveryCrashJournalCodec.DecodeCandidate(",
            "RecoveryCrashJournalReconciler.Analyze(",
            "HasStableDistinctFileIds()",
            "ReopenedIdentityMismatch",
            "JournalArtifactConflict"
        ):
            self.assertIn(token, self.code)

    def test_distinguishes_orphans_incomplete_and_corrupt(self):
        for label in (
            "AnchorRejected", "InvalidJournal", "NeedsNativeReadback",
            "OrphanRequiresReview", "NoJournalEvidence",
            "IncompleteOperation", "MatchedClaimsOnly"
        ):
            self.assertIn(label, self.code)

    def test_no_filesystem_mutation_or_live_store_wiring(self):
        for forbidden in ("File.Delete(", "File.Move(", "File.Write", "File.Open(",
                          "FileStream(", "DllImport(", "LibraryImport(",
                          "Directory.Create", "unlinkat(", "renameat2("):
            self.assertNotIn(forbidden, self.code)
        self.assertNotIn("RecoveryRestartCheckpointReview.", self.store)

    def test_no_restore_delete_publish_permission_from_claims(self):
        for token in (
            "NativeRestartAttestationEnabled => false;",
            "HasTrustedPersistentAnchor => false;",
            "HasIndependentlyAuthenticatedJournal => false;",
            "CanRestore => false;", "CanDelete => false;",
            "CanResumePublication => false;", "ReleaseReady => false;"
        ):
            self.assertIn(token, self.code)

    def test_fourteen_restart_mstest_authored(self):
        self.assertEqual(14, self.tests.count("[TestMethod]"))
        self.assertIn("TruncatedJournalCannotMatchProtectedHead", self.tests)
        self.assertIn("ReopenedCopyIdChangedIsReportedNotRestored", self.tests)

    def test_runner_171_manual_gates_and_release_denied(self):
        step = 'CommandStep("recovery-durable-checkpoint-blocks-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn('"-p", "test_recovery_durable_checkpoint_blocks_contract.py"', self.runner)
        for gate in (
            "gd31j27bz-windows-journal-dir-sync-manual-e2e",
            "gd31j27bz-astra-ext4-journal-fsync-manual-e2e",
            "gd31j27bz-windows-trusted-key-rollback-manual-e2e",
            "gd31j27bz-astra-protected-key-monotonic-manual-e2e",
            "gd31j27bz-smb-crosshost-journal-consistency-manual-e2e",
            "gd31j27bz-restart-native-identity-reopen-manual-e2e",
            "gd31j27bz-powerloss-between-journal-anchor-manual-e2e",
            "gd31j27bz-original-worm-immutability-manual-e2e"
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
