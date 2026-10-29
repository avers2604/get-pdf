"""GD-31J.31 offline source contracts for manual synthetic recovery reports.

This file checks source contracts only. It is NOT a filesystem, power-loss,
CEF, .NET runtime or security penetration test.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"

class SyntheticRecoveryReportContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source=(OFFICE/"EuroOfficeSyntheticRecoveryReport.cs").read_text(encoding="utf-8")
        cls.journal=(OFFICE/"EuroOfficeSyntheticSnapshotJournal.cs").read_text(encoding="utf-8")
        cls.tests=(ROOT/"tests/GetDocument.Office.Tests/EuroOfficeSyntheticRecoveryReportTests.cs").read_text(encoding="utf-8")

    def test_report_is_explicit_read_only_and_page_bounded(self):
        for required in (
            "getdoc-synthetic-recovery-report/1",
            "private const int PageSize = 32;",
            "private const int MaxJsonChars = 16384;",
            "internal Page(int pageIndex, int pageCount, int totalFindings,",
            "Array.AsReadOnly(counts), Array.AsReadOnly(page)",
            "EuroOfficeSyntheticSnapshotJournal.Inspect(stagingRoot, labApproved: true)",
            "if (!labApproved)",
            "if (pageIndex is < 0 or > 7)",
            "if (pageIndex >= pages)",
            "if (json.Length > MaxJsonChars)",
            "public bool ReadOnly => true",
            "public bool ManualReviewRequired => true",
            "public bool CleanupAuthorized => false",
            "public bool AuthorizedForPublication => false",
            "public bool DurabilityProven => false",
        ):
            self.assertIn(required, self.source)

    def test_no_untrusted_names_absolute_paths_content_or_implicit_export(self):
        source=self.source
        self.assertIn("x.AttemptId?.ToString(\"N\")", source)
        self.assertIn("SafeStage(x.LastStage)", source)
        self.assertIn("GuidanceFor(x.State)", source)
        self.assertNotIn("x.PendingName", source)
        self.assertNotIn("x.FinalName", source)
        self.assertNotIn("string path =", source)
        self.assertNotIn("File.WriteAllText(", source)
        self.assertNotIn("File.WriteAllBytes(", source)
        self.assertNotIn("File.Delete(", source)
        self.assertNotIn("File.Move(", source)
        self.assertNotIn("Directory.CreateDirectory(", source)
        self.assertNotIn("Process.Start(", source)
        self.assertNotIn("Timer(", source)
        self.assertNotIn("Task.Run(", source)

    def test_observer_is_only_bounded_inspector(self):
        self.assertIn("Directory.EnumerateFileSystemEntries(", self.journal)
        self.assertIn("if (++scanned > 2048)", self.journal)
        self.assertIn("if (journals.Count + pending.Count > MaxItems)", self.journal)
        self.assertIn("TrustedForDeletion => false", self.journal)
        self.assertIn("AuthorizedForPublication => false", self.journal)

    def test_tests_cover_privacy_paging_and_refusal(self):
        for required in (
            "ManualReportIsReadOnlyAndNeverContainsPathsOrUntrustedNames",
            "BoundedPaginationIncludesSummaryOfAllEvents",
            "EmptyPageIsStillUntrustedAndFailsClosed",
            "CorruptJournalIsGenericObservationWithoutLeakingInputText",
            "RootLinkIsRejectedAndNoExportFileIsCreated"
        ):
            self.assertIn(required, self.tests)

if __name__=="__main__":
    unittest.main()
