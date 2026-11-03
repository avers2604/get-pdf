"""GD-31J.27bk: source-only contracts for no-delete rollback and orphan listing.

These checks inspect text. They do NOT compile C#, execute MSTest, prove
actual process-kill semantics, or authorize any automatic cleanup.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
INVENTORY = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryDetachedWorkspaceInspector.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/RecoveryDetachedWorkspaceInspectorTests.cs"
STORE_TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryStoreTests.cs"
LINK_TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryStoreLinkSafetyTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryOrphanInventorySourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = STORE.read_text(encoding="utf-8")
        cls.inventory = INVENTORY.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.store_tests = STORE_TESTS.read_text(encoding="utf-8")
        cls.link_tests = LINK_TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.capture = cls.store.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        cls.inspect = cls.inventory.split(
            "internal static RecoveryDetachedWorkspaceInventory Inspect(", 1
        )[1].split("private static bool TryClassify(", 1)[0]

    def test_capture_rejects_path_based_cleanup_without_ownership_receipt(self):
        self.assertIn("await exportCopy(temporaryCopy);", self.capture)
        self.assertIn("File.Move(temporaryCopy, finalCopy);", self.capture)
        self.assertIn("File.Move(temporaryManifest, finalManifest);", self.capture)
        for forbidden in (
            "TryDelete(temporaryCopy);", "TryDelete(temporaryManifest);",
            "TryDelete(finalCopy);", "File.Delete(", "Directory.Delete(",
            "File.Replace(", "File.Move(finalCopy,",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.capture)
        self.assertIn("If the manifest was committed before Prune failed, keep it.",
                      self.capture)

    def test_no_restore_of_uncommitted_exported_bytes(self):
        self.assertIn("FileMode.CreateNew", self.capture)
        self.assertIn("cancellationToken.ThrowIfCancellationRequested();", self.capture)
        self.assertLess(self.capture.index("VerifyAndHashExportAsync("),
                        self.capture.index("File.Move(temporaryCopy, finalCopy);"))
        self.assertLess(self.capture.index("File.Move(temporaryCopy, finalCopy);"),
                        self.capture.index("File.Move(temporaryManifest, finalManifest);"))
        self.assertIn("public OfficeRecoveryCopy? FindLatest(", self.store)
        self.assertIn("ReadManifest(manifestPath)", self.store)

    def test_inventory_is_name_only_read_only_and_never_recursive(self):
        self.assertIn("internal static RecoveryDetachedWorkspaceInventory Inspect(", self.inventory)
        self.assertIn("Directory.EnumerateFileSystemEntries(", self.inspect)
        self.assertIn("SearchOption.TopDirectoryOnly", self.inspect)
        self.assertIn("Path.TrimEndingDirectorySeparator(", self.inspect)
        self.assertIn("DirectoryInfo(candidatePath)", self.inspect)
        self.assertIn("new RecoveryDetachedWorkspaceCandidate(", self.inspect)
        self.assertIn("name, kind, status, changedUtc", self.inspect)
        for forbidden in (
            "File.Delete(", "Directory.Delete(", "File.Move(", "File.Copy(",
            "File.ReadAllBytes(", "File.ReadAllText(", "File.Open(",
            "new FileStream(", "Directory.CreateDirectory(", "CaptureAsync(",
            "Discard(", "Prune(", "Process.GetProcesses", "File.Replace(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.inventory)

    def test_inventory_bounded_all_entries_and_link_status_not_authority(self):
        for marker in (
            "maxEntries is < 1 or > 1024",
            "if (++visited > maxEntries)",
            "truncated = true;",
            "cancellationToken.ThrowIfCancellationRequested();",
            "Path.GetDirectoryName(candidatePath)",
            "RejectLinkedDirectoryTree(root)",
            "if (rootMetadata.LinkTarget is not null)",
            "metadata.LinkTarget is not null",
            "FileAttributes.ReparsePoint",
            "RecoveryDetachedWorkspaceStatus.UntrustedLinkOrNonDirectory",
            "RecoveryDetachedWorkspaceStatus.MetadataUnavailable",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.inventory)
        self.assertNotIn("TimeSpan.FromDays(", self.inventory)
        self.assertNotIn("Age >=", self.inventory)

    def test_only_exact_private_prefixes_with_nonce_like_suffixes(self):
        self.assertIn('const string publisherPrefix = "getdoc-recovery-private-";',
                      self.inventory)
        self.assertIn('const string consumerPrefix = "getdoc-consumer-owned-";',
                      self.inventory)
        self.assertIn("StringComparison.Ordinal", self.inventory)
        self.assertIn("suffix.Length is >= 6 and <= 80", self.inventory)
        self.assertNotIn("Path.Combine(root, name + \".docx\")", self.inventory)

    def test_synthetic_tests_cover_faults_no_mutation_and_untrusted_links(self):
        for name in (
            "MissingRootReturnsEmptyWithoutCreatingAnyDirectory",
            "OnlyRecognizedPrivateNamesAreListedWithoutOpeningContents",
            "PrefixedRegularFileIsNeverMistakenForPrivateWorkspace",
            "EnumerationBudgetIncludesUnrelatedEntriesAndReportsTruncation",
            "ForeignOrNestedEnumeratorPathIsRejectedBeforeAnyMetadataRead",
            "LazyEnumerationIOExceptionPropagatesInsteadOfReportingEmptyScan",
            "CancelledScanDoesNotEnumerateOrMutate",
            "LinkedWorkspaceIsMarkedUntrustedIfSymlinksAreAvailable",
            "LinkedTemporaryRootIsRejectedIfSymlinksAreAvailable",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(9, self.tests.count("[TestMethod]"))
        self.assertIn("CancelledExporterLeavesUntrustedCopyForReadOnlyInventory",
                      self.store_tests)
        self.assertIn("OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile",
                      self.link_tests)

    def test_runner_has_manual_platform_checks_and_release_off(self):
        step = 'CommandStep("recovery-orphan-inventory-source-tests"'
        self.assertEqual(1, self.runner.count(step))
        self.assertIn('"-p", "test_recovery_orphan_inventory_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index(step),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bk-path-rollback-windows-ntfs-manual-e2e",
            "gd31j27bk-orphan-inventory-astra-ext4-manual-e2e",
            "gd31j27bk-smb-failure-no-auto-delete-manual-e2e",
        ):
            with self.subTest(gate=gate):
                self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
