"""GD-31J.32 source contracts for synthetic exception crash checkpoints.

Does NOT execute .NET tests, start CEF, terminate processes, or power-cycle a VM.
All real checks remain deferred to the explicit offline laboratory.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"

class SyntheticSnapshotFaultSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.snapshot=(OFFICE/"EuroOfficeSyntheticReviewedSnapshot.cs").read_text(encoding="utf-8")
        cls.journal=(OFFICE/"EuroOfficeSyntheticSnapshotJournal.cs").read_text(encoding="utf-8")
        cls.report=(OFFICE/"EuroOfficeSyntheticRecoveryReport.cs").read_text(encoding="utf-8")
        cls.tests=(ROOT/"tests/GetDocument.Office.Tests/EuroOfficeSyntheticSnapshotFaultCheckpointTests.cs").read_text(encoding="utf-8")

    def test_failing_barrier_is_reachable_only_through_internal_test_seam(self):
        src=self.snapshot
        self.assertIn('internal enum EuroOfficeSyntheticSnapshotFaultCheckpoint',src)
        self.assertIn('internal static Receipt CommitWithFaultProbe(',src)
        self.assertIn('Action<EuroOfficeSyntheticSnapshotFaultCheckpoint> faultProbe',src)
        self.assertIn('if (!labApproved)',src)
        self.assertIn('ArgumentNullException.ThrowIfNull(faultProbe)',src)
        self.assertIn('CommitCore(stagingRoot, review, labApproved, faultProbe)',src)
        self.assertIn('CommitCore(stagingRoot, review, labApproved, faultProbe: null)',src)
        self.assertNotIn('public static Receipt CommitWithFaultProbe(',src)
        self.assertIn('native.FailureCode != "candidate_untrusted"',src)
        self.assertIn('!native.CorrelatedNativeCallbacks',src)

    def test_fault_boundaries_follow_monotonic_journal_and_link_order(self):
        src=self.snapshot
        markers=[
            'EuroOfficeSyntheticSnapshotJournal.Begin(',
            'AfterIntentPersisted);',
            'output.Flush(flushToDisk: true)',
            'AfterPendingFileFlushed);',
            'EuroOfficeSyntheticSnapshotJournal.Append(attempt, "pending_flushed")',
            'AfterPendingJournalFlushed);',
            'File.CreateHardLink(destination, pending)',
            'AfterHardLinkCreated);',
            'EuroOfficeSyntheticSnapshotJournal.Append(attempt, "linked")',
            'AfterLinkedJournalFlushed);',
            'File.Delete(pending)',
            'AfterPendingLinkRemoved);',
            'HashBounded(destination, out long committedLength)',
            'AfterFinalHashVerified);',
            'EuroOfficeSyntheticSnapshotJournal.Append(attempt, "completed")',
            'AfterCompletedJournalFlushed);'
        ]
        positions=[src.index(marker) for marker in markers]
        self.assertEqual(positions,sorted(positions),
            "Crash checkpoints must be strictly ordered in real CommitCore.")
        self.assertIn('FileMode.CreateNew',src)
        self.assertIn('FileOptions.WriteThrough',src)
        self.assertIn('HashBounded(pending, out long preLinkLength)',src)
        self.assertIn('EuroOfficeSyntheticSnapshotJournal.RequireLastStage(attempt, "pending_flushed")',src)
        self.assertLess(src.index('RequireLastStage(attempt, "pending_flushed")'),
                        src.index('File.CreateHardLink(destination, pending)'))
        self.assertIn('internal static void RequireLastStage(', self.journal)
        self.assertIn('output.Flush(flushToDisk: true)',src)
        self.assertIn('public bool DurableAcrossPowerLoss => false',src)

    def test_regressions_check_all_checkpoint_outcomes_without_file_cleanup(self):
        for stage in (
            "AfterIntentPersisted",
            "AfterPendingFileFlushed",
            "AfterPendingJournalFlushed",
            "AfterHardLinkCreated",
            "AfterLinkedJournalFlushed",
            "AfterPendingLinkRemoved",
            "AfterFinalHashVerified",
            "AfterCompletedJournalFlushed",
            "EuroOfficeSyntheticSnapshotJournal.Inspect(",
            "EuroOfficeSyntheticRecoveryReport.Build(",
            "Assert.AreEqual(sourceBefore, lab.Hash(lab.Source))",
            "Assert.AreEqual(candidateBefore, lab.Hash(lab.Candidate))",
            "Assert.IsFalse(report.CleanupAuthorized)",
            "Assert.IsFalse(report.DurabilityProven)",
            "DisabledLabCannotReachFaultHookOrWriteAnyJournal",
            "NullHookOrUnapprovedRevokedEvidenceCannotGainWriteAuthority",
            "MutatingPendingBetweenJournalFlushAndHardLinkMustNotCreateFinalName",
            "MutatingJournalBetweenPendingFlushAndLinkMustNotCreateFinalName"
        ):
            self.assertIn(stage,self.tests)
        self.assertIn('public bool TrustedForDeletion => false',self.journal)
        self.assertIn('public bool AuthorizedForPublication => false',self.report)
        self.assertNotIn('File.Delete(',self.report)

if __name__=="__main__":
    unittest.main()
