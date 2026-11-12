"""GD-31J.27ca: offline source contracts only, no .NET runtime or OS I/O.

Do not confuse these structural checks with native NTFS/Astra/SMB testing,
crash durability, read-only handle integration, or independent journal trust.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
TESTS = ROOT / "tests/GetDocument.Office.Tests"
RUNNER = ROOT / "scripts/office_acceptance.py"


class BoundedEnvelopeSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = (OFFICE / "RecoveryCrashJournalWireEnvelope.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryCrashJournalWireEnvelopeTests.cs").read_text(encoding="utf-8")

    def test_exact_header_version_and_bound(self):
        for value in (
            "HeaderBytes = 48", "FormatVersion = 1",
            "RecoveryCrashJournalCodec.MaxEnvelopeBytes",
            'Encoding.ASCII.GetBytes("GD31JCA1")',
            "payloadBytes", "wire.Length != HeaderBytes + (int)payloadBytes",
        ):
            self.assertIn(value, self.src)

    def test_sha256_integrity_and_canonical_validation(self):
        for value in (
            "SHA256.HashData(journalBytes)", "SHA256.HashData(payload)",
            "CryptographicOperations.FixedTimeEquals(",
            "RecoveryCrashJournalCodec.DecodeCandidate(",
            "CryptographicOperations.ZeroMemory(computed)",
            "CryptographicOperations.ZeroMemory(digest)",
        ):
            self.assertIn(value, self.src)

    def test_no_persistence_or_mutation_in_envelope(self):
        for forbidden in (
            "File.Open", "File.Delete", "File.Move", "File.Write",
            "FileStream(", "Directory.Create", "DllImport(", "LibraryImport(",
            "File.Replace(", "unlinkat(", "renameat2(",
        ):
            self.assertNotIn(forbidden, self.src)
        for flag in (
            "HasIndependentlyTrustedSigner => false;",
            "CanAuthorizeMutation => false;", "CanAuthorizeCleanup => false;",
        ):
            self.assertIn(flag, self.src)

    def test_eleven_envelope_mstests_authored(self):
        self.assertEqual(11, self.tests.count("[TestMethod]"))
        for name in (
            "CanonicalJournalRoundTripIsExact",
            "AppendedBytesCannotBeSilentlyIgnored",
            "NoncanonicalJsonIsRejectedBeforeEncoding",
            "ARecomputedUnkeyedDigestStillCannotAuthorizeRecovery",
        ):
            self.assertIn(name, self.tests)


class HeldHandleReadbackSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = (OFFICE / "RecoveryRestartHeldHandleReadback.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryRestartHeldHandleReadbackTests.cs").read_text(encoding="utf-8")

    def test_six_caller_owned_handles_and_native_probes(self):
        for value in (
            "SafeFileHandle parentAtOpen", "SafeFileHandle parentAtRecheck",
            "SafeFileHandle copyAtOpen", "SafeFileHandle copyAtRecheck",
            "SafeFileHandle manifestAtOpen", "SafeFileHandle manifestAtRecheck",
            "RecoveryNativeHandleIdProbe.ReadFromExistingHandle(",
            "snapshot.HasStableDistinctFileIds()",
        ):
            self.assertIn(value, self.src)

    def test_closed_and_cancelled_handles_rejected(self):
        self.assertIn("cancellationToken.ThrowIfCancellationRequested();", self.src)
        self.assertIn("handle.IsClosed || handle.IsInvalid", self.src)
        self.assertIn("ObjectDisposedException", self.src)

    def test_no_reopen_by_path_and_no_authorizations(self):
        for forbidden in (
            "File.Open", "File.Move", "File.Delete", "File.Write",
            "FileStream(", "Directory.Delete", "GetFileInformationByHandleEx(",
            "DllImport(", "LibraryImport(", "unlinkat(", "renameat2(",
        ):
            self.assertNotIn(forbidden, self.src)
        for flag in (
            "NativeNameBindingVerificationEnabled => false;",
            "HasVerifiedNamesInDirectory => false;",
            "HasVerifiedExclusiveWriterLock => false;",
            "HasVerifiedCrashDurability => false;",
            "CanRestore => false;", "CanDelete => false;",
            "CanPublish => false;", "ReleaseReady => false;",
        ):
            self.assertIn(flag, self.src)

    def test_six_readback_mstests_authored(self):
        self.assertEqual(6, self.tests.count("[TestMethod]"))
        self.assertIn("ChangedCopyReopenIsRejected", self.tests)
        self.assertIn("CancellationPreventsAnyNativeProbe", self.tests)


class RestartIntegrationSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = (OFFICE / "RecoveryRestartCheckpointReview.cs").read_text(encoding="utf-8")
        cls.store = (OFFICE / "OfficeRecoveryStore.cs").read_text(encoding="utf-8")
        cls.tests = (TESTS / "RecoveryRestartWireEnvelopeIntegrationTests.cs").read_text(encoding="utf-8")

    def test_transport_decode_precedes_old_diagnostic_review(self):
        self.assertIn("internal static RecoveryRestartReviewResult InspectEnvelope(", self.src)
        self.assertIn("RecoveryCrashJournalWireEnvelope.DecodeCandidate(", self.src)
        self.assertIn("RecoveryRestartFinding.InvalidJournal", self.src)
        self.assertIn("return Inspect(journal, anchor, callerSuppliedKey,", self.src)

    def test_live_recovery_store_not_wired(self):
        self.assertNotIn("RecoveryCrashJournalWireEnvelope.", self.store)
        self.assertNotIn("RecoveryRestartHeldHandleReadback.", self.store)
        self.assertNotIn("RecoveryRestartCheckpointReview.", self.store)

    def test_four_integration_mstests_authored(self):
        self.assertEqual(4, self.tests.count("[TestMethod]"))
        self.assertIn("WellFormedEnvelopeNeverOverridesWrongAnchorKey", self.tests)
        self.assertIn("DamagedEnvelopeIsInvalidJournalNotSuccessfulRestart", self.tests)


class OfflineRunnerContracts(unittest.TestCase):
    def test_runner_has_source_test_before_restore(self):
        source = RUNNER.read_text(encoding="utf-8")
        marker = 'CommandStep("recovery-journal-wire-readback-source-tests"'
        self.assertEqual(1, source.count(marker))
        self.assertLess(source.index(marker), source.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_journal_wire_readback_contract.py"', source
        )
        self.assertIn('"releaseReady": False', source)

    def test_eight_manual_platform_gates_are_not_claimed_executed(self):
        source = RUNNER.read_text(encoding="utf-8")
        for name in (
            "gd31j27ca-windows-ntfs-journal-envelope-powercut-manual-e2e",
            "gd31j27ca-astra-ext4-journal-envelope-torn-write-manual-e2e",
            "gd31j27ca-windows-existing-handles-reopen-identity-manual-e2e",
            "gd31j27ca-astra-existing-fds-rename-race-manual-e2e",
            "gd31j27ca-smb-distributed-lock-crosshost-manual-e2e",
            "gd31j27ca-mount-identity-bind-reparent-manual-e2e",
            "gd31j27ca-trusted-anchor-rollback-manual-e2e",
            "gd31j27ca-original-worm-no-change-manual-e2e",
        ):
            self.assertIn(name, source)


if __name__ == "__main__":
    unittest.main()
