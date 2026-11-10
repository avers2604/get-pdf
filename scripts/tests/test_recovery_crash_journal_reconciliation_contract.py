"""GD-31J.27by: source-only crash journal replay and reconciliation guards.

These tests inspect C# text only. They do not execute dotnet or real
Windows/Astra/SMB I/O; they cannot authorize cleanup or restoration.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
OFFICE = ROOT / "src/GetDocument.Office/LibreOffice"
JOURNAL = OFFICE / "RecoveryCrashJournalReconciler.cs"
CODEC = OFFICE / "RecoveryCrashJournalCodec.cs"
PLANNER = OFFICE / "RecoveryNativeMutationPlanner.cs"
STORE = OFFICE / "OfficeRecoveryStore.cs"
PAIR = OFFICE / "RecoveryPairMutationGate.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryCrashJournalReconciliationTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryCrashJournalReconciliationContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.journal = JOURNAL.read_text(encoding="utf-8")
        cls.codec = CODEC.read_text(encoding="utf-8")
        cls.planner = PLANNER.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.pair = PAIR.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")

    def test_synthetic_journal_has_exact_limits_and_genesis(self):
        self.assertIn("internal const int FormatVersion = 1;", self.journal)
        self.assertIn("internal const int MaxFrames = 16;", self.journal)
        self.assertIn("GenesisSha256", self.journal)
        self.assertIn("MaxEnvelopeBytes = 8192;", self.codec)
        self.assertIn("frames.Count > MaxFrames", self.journal)
        self.assertIn("frames.Count > RecoveryCrashJournalReconciler.MaxFrames", self.codec)

    def test_canonical_invariant_sha_chain_not_authentication(self):
        for token in (
            "SHA256.HashData(Encoding.ASCII.GetBytes(canonical))",
            "FormattableString.Invariant(",
            "string.Equals(frame.PreviousSha256, previous,",
            "string.Equals(calculated, frame.FrameSha256,",
            "string.Equals(expectedJournalHeadSha256, previous,",
            "HasIndependentJournalAuthentication => false;",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.journal)

    def test_strict_transaction_kind_index_version_checks(self):
        for token in (
            "frame.Version != FormatVersion",
            "frame.TransactionId != expectedTransactionId",
            "frame.Kind != expectedKind",
            "frame.Sequence != i + 1",
            "IsUppercaseSha256(frame.FrameSha256)",
            "RecoveryNativeMutationPlanner.Start(",
            "RecoveryNativeMutationPlanner.Advance(plan, frame.Event)",
            "frames.Count > MaxFrames",
        ):
            self.assertIn(token, self.journal)

    def test_canonical_byte_codec_rejects_ambiguous_json(self):
        for token in (
            "JsonDocument.Parse(bytes,",
            "JsonValueKind.Array",
            "GetArrayLength()",
            "JsonSerializer.Deserialize<RecoveryCrashJournalFrame[]>(bytes.Span)",
            "JsonSerializer.SerializeToUtf8Bytes(frames)",
            "bytes.Span.SequenceEqual(canonical)",
            "MaxDepth = 8",
            "CanAuthorizeFileMutation => false;",
        ):
            self.assertIn(token, self.codec)

    def test_reconciler_distinguishes_missing_from_unknown(self):
        for token in (
            "RecoveryCrashArtifactClaim.NotObserved",
            "RecoveryCrashArtifactClaim.MetadataUnavailable",
            "RecoveryCrashArtifactClaim.Missing",
            "RecoveryCrashArtifactClaim.RegularFileObserved",
            "RecoveryCrashArtifactClaim.UntrustedLinkOrNonFile",
            "RecoveryCrashReconciliationDisposition.CopyWithoutManifest",
            "RecoveryCrashReconciliationDisposition.ManifestWithoutCopy",
            "RecoveryCrashReconciliationDisposition.JournalConflictsWithArtifactObservation",
            "RecoveryCrashReconciliationDisposition.NoJournalEvidence",
        ):
            self.assertIn(token, self.journal)

    def test_no_write_delete_restore_or_process_execution(self):
        for file in (self.journal, self.codec):
            for forbidden in (
                "File.Delete(", "File.Move(", "File.Write", "File.ReadAll",
                "FileStream(", "Directory.Delete(", "Directory.Create",
                "unlinkat(", "renameat2(", "DllImport(", "LibraryImport(",
                "Process.Start(", "System.IO.File.Open(",
            ):
                with self.subTest(forbidden=forbidden):
                    self.assertNotIn(forbidden, file)
        for token in (
            "CanAuthorizeRestoration => false;",
            "CanAuthorizeCleanup => false;",
            "CanAuthorizePublication => false;",
        ):
            self.assertIn(token, self.journal)

    def test_no_production_wiring_or_authority_escalation(self):
        for string in ("RecoveryCrashJournalReconciler.", "RecoveryCrashJournalCodec."):
            self.assertNotIn(string, self.store)
            self.assertNotIn(string, self.pair)
            self.assertNotIn(string, self.planner)
        self.assertIn("NativeBackendEnabled => false;", self.planner)
        self.assertIn("CanDeleteByPath => false;", self.pair)
        self.assertIn("CanPublishByPath => false;", self.pair)

    def test_mstest_covers_partial_crash_tamper_and_ambiguity(self):
        self.assertEqual(23, self.tests.count("[TestMethod]"))
        for token in (
            "NoJournalIsUnknownAndNeverAReasonToDelete",
            "CopyPublishedWithoutManifestIsOrphanForReviewNotRollback",
            "CompletedDeleteClaimWithRemainingFilesIsAContradiction",
            "UntrustedLinkOrNonFileTakesPriorityOverOtherFindings",
            "HashChainDetectsSimpleMutationGapAndReordering",
            "TruncationIsDetectableOnlyWithExternalHeadAndNoAuthorityFollows",
            "DecoderRejectsNoncanonicalWhitespaceAndUnknownProperties",
            "CanonicalJsonDoesNotMeanAuthenticOrSemanticallyValid",
        ):
            self.assertIn(token, self.tests)

    def test_no_unverified_restore_or_cleanup_policy(self):
        self.assertIn("ClaimedCompleteUnverified", self.journal)
        self.assertIn("PartialMutationClaim", self.journal)
        self.assertIn("InterruptedBeforeMutationClaim", self.journal)
        self.assertNotIn("CanAuthorizeRestoration => true", self.journal)
        self.assertNotIn("CanAuthorizeCleanup => true", self.journal)
        self.assertNotIn("CanAuthorizePublication => true", self.journal)
        self.assertNotIn("CanAuthorizeFileMutation => true", self.codec)

    def test_runner_manual_gates_and_release_stay_blocked(self):
        step = 'CommandStep("recovery-crash-journal-reconcile-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        self.assertIn(
            '"-p", "test_recovery_crash_journal_reconciliation_contract.py"',
            self.runner)
        for gate in (
            "gd31j27by-windows-crash-journal-truncation-manual-e2e",
            "gd31j27by-astra-ext4-journal-fsync-powerloss-manual-e2e",
            "gd31j27by-smb-cross-host-journal-replay-manual-e2e",
            "gd31j27by-restart-artifact-identity-review-manual-e2e",
            "gd31j27by-original-worm-preservation-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
