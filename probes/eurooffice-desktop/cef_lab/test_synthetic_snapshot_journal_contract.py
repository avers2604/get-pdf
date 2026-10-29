"""GD-31J.30 read-only synthetic journal and orphan source contracts.

These tests are source-only; no CEF, Qt, .NET, filesystem, crash or
durability tests are executed by this file on its own.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
LAB=ROOT/"probes/eurooffice-desktop/cef_lab"

class SyntheticJournalContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.journal=(OFFICE/"EuroOfficeSyntheticSnapshotJournal.cs").read_text(encoding="utf-8")
        cls.commit=(OFFICE/"EuroOfficeSyntheticReviewedSnapshot.cs").read_text(encoding="utf-8")
        cls.tests=(ROOT/"tests/GetDocument.Office.Tests/EuroOfficeSyntheticSnapshotJournalTests.cs").read_text(encoding="utf-8")

    def test_attempt_journal_written_before_first_pending_write(self):
        source=self.commit
        self.assertLess(source.index("EuroOfficeSyntheticSnapshotJournal.Begin("),
                        source.index("new FileStream(\n            pending, FileMode.CreateNew"))
        self.assertLess(source.index('Append(attempt, "pending_flushed")'),
                        source.index("EuroOfficeNativeHardLinkNoReplace.Create(destination, pending)"))
        self.assertLess(source.index("EuroOfficeNativeHardLinkNoReplace.Create(destination, pending)"),
                        source.index('Append(attempt, "linked")'))
        self.assertLess(source.index('Append(attempt, "linked")'),
                        source.index("File.Delete(pending)"))
        self.assertLess(source.index("File.Delete(pending)"),
                        source.index('Append(attempt, "completed")'))
        self.assertIn('internal static void RequireRegularPath(',source)

    def test_observer_is_bounded_read_only_and_rejects_journal_corruption(self):
        journal=self.journal
        for marker in (
            "FileMode.CreateNew", "FileOptions.WriteThrough",
            "Flush(flushToDisk: true)", 'Schema = "getdoc-synthetic-reviewed-journal/1"',
            "MaxJournalBytes = 8192", "MaxItems = 256",
            'stage: "intent"', '"pending_flushed"', '"linked"', '"completed"',
            "JsonDocument.Parse(", "TrustedForDeletion => false",
            "AuthorizedForPublication => false",
            "EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath",
            "Directory.EnumerateFileSystemEntries(", "SearchOption.TopDirectoryOnly",
            "Observation.MalformedJournal", "Observation.UnknownPending",
            "if (!labApproved)"
        ):
            self.assertIn(marker,journal)
        # No automatic remediation, cleanup, quarantine move or task scheduling
        for marker in ("File.Delete(", "File.Move(", "File.Copy(",
                       "Directory.Delete(", "Directory.CreateDirectory(",
                       "Process.Start(", "Task.Run("):
            self.assertNotIn(marker,journal)
        self.assertNotIn("SearchOption.AllDirectories",journal)

    def test_regressions_cover_crash_outcomes_without_deleting_user_files(self):
        tests=self.tests
        for name in ("IntentWithPendingIsObservationOnlyAndNeverAutoCleanup",
                     "CompleteJournalIsOnlyObservedAndLeavesOriginalUntouched",
                     "PostLinkCrashAndUnknownPendingNeverTriggerDeletion",
                     "MissingOrMalformedJournalIsFlaggedWithoutDeletingIt",
                     "JournalMustFollowSingleForwardOnlyStateMachine",
                     "InspectionIsExplicitlyDisabledAndRequiresNonLinkedLocalFolder",
                     "InspectRefusesUnexpectedOrUnboundedDirectory"):
            self.assertIn(name, tests)

if __name__=="__main__":
    unittest.main()
