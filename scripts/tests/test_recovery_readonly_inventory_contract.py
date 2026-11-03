"""Source-only guard for GD-31J.27ao. Not a C# test or platform attestation."""
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TEST = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryIncompleteInventoryTests.cs"


class RecoveryInventorySourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = SOURCE.read_text(encoding="utf-8")
        cls.full_source = source
        cls.inspect = source.split(
            "public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1
        )[1].split("private static bool TryClassifyIncompleteName(", 1)[0]
        cls.classifier = source.split(
            "private static bool TryClassifyIncompleteName(", 1
        )[1].split("public OfficeRecoveryCopy? FindLatest(", 1)[0]
        cls.tests = TEST.read_text(encoding="utf-8")

    def test_never_mutates_or_reads_contents(self):
        for forbidden in (
            "File.Delete(", "Directory.Delete(", "TryDelete(", "Prune(",
            "Discard(", "CaptureAsync(", "File.Move(", "File.Copy(",
            "File.Write", "File.Read", "new FileStream(",
            "ZipFile.OpenRead(", "JsonSerializer.Deserialize",
            "Directory.CreateDirectory(", "File.CreateSymbolicLink(",
            "HttpClient(", "Process.Start(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.inspect)

    def test_source_scoped_and_non_recursive_with_fixed_budget(self):
        for expected in (
            "FolderFor(source)",
            "RejectLinkedDirectories(folder)",
            "if (maxEntries is < 1 or > 1024)",
            "SearchOption.TopDirectoryOnly",
            "if (++visited > maxEntries)",
            "cancellationToken.ThrowIfCancellationRequested()",
            "truncated = true",
            "FileInfo(entry)",
            "metadata.LinkTarget is not null",
            "FileAttributes.ReparsePoint",
            "FileAttributes.Directory",
            "OfficeRecoveryIncompleteStatus.MetadataUnavailable",
            "OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile",
        ):
            with self.subTest(expected=expected):
                self.assertIn(expected, self.inspect)

    def test_only_exact_guid_temp_names_are_candidates(self):
        for expected in (
            'tail.Equals(".part" + extension, StringComparison.OrdinalIgnoreCase)',
            'tail.Equals(".json.tmp", StringComparison.OrdinalIgnoreCase)',
            'Guid.TryParseExact(fileName[..GuidLength], "N", out _)',
        ):
            with self.subTest(expected=expected):
                self.assertIn(expected, self.classifier)

    def test_regressions_cover_privacy_and_budget(self):
        for required in (
            "NoRecoveryDirectoryIsEmptyWithoutCreatingIt",
            "FindsOnlyExactGuidStagedCopyAndTemporaryManifestWithoutChangingBytes",
            "LinkedTemporaryManifestIsListedAsUntrustedWithoutReadingItsTarget",
            "CandidateDirectoryIsNeverEnteredOrTreatedAsFile",
            "EntryBudgetIsBoundedAndReportedAsTruncated",
            "DifferentDocumentsNeverShareTheSameRecoveryListing",
            "LinkedRecoveryRootIsRejectedBeforeEnumeration",
            "InvalidBudgetAndUnsupportedFileTypeFailBeforeReadingAnything",
            "DanglingSymlinkIsReportedAsUntrustedWithoutFollowingIt",
            "CancelledInventoryFailsInsteadOfReportingEmpty",
        ):
            with self.subTest(required=required):
                self.assertIn(required, self.tests)
        self.assertEqual(10, self.tests.count("[TestMethod]"))


if __name__ == "__main__":
    unittest.main()
