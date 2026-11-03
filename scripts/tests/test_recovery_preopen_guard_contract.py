"""GD-31J.27bc source-only pre-open and post-open path checks.

These are textual contracts, NOT evidence of OS file-identity guarantees,
a C# build, native no-follow semantics, or real SMB behavior.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryPreOpenIdentityTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryPreOpenSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.open = cls.store.split(
            "private static FileStream OpenRecoveryReadStream(", 1
        )[1].split("internal static bool VerifyOpenedPackage(", 1)[0]
        cls.capture = cls.store.split(
            "public async Task<OfficeRecoveryCopy> CaptureAsync(", 1
        )[1].split("public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1)[0]
        cls.copy = cls.store.split(
            "private OfficeRecoveryCopy? ReadValidCopy(", 1
        )[1].split("private void Prune(", 1)[0]

    def test_test_hook_only_simulates_between_preflight_and_open(self):
        self.assertIn("internal static FileStream OpenRecoveryReadStreamForTest(", self.store)
        self.assertIn("Action<string> beforeOpen", self.store)
        self.assertIn("return OpenRecoveryReadStream(path, FileOptions.None, beforeOpen);",
                      self.store)
        self.assertIn("Action<string>? beforeOpen = null", self.open)
        self.assertIn("beforeOpen?.Invoke(path);", self.open)
        self.assertLess(self.open.index("file.Refresh();"),
                        self.open.index("beforeOpen?.Invoke(path);"))
        self.assertLess(self.open.index("beforeOpen?.Invoke(path);"),
                        self.open.index("new FileStream(path, FileMode.Open, FileAccess.Read,"))

    def test_postopen_path_checks_run_before_handle_is_returned(self):
        for marker in (
            "RejectLinkedDirectories(Path.GetDirectoryName(path)!);",
            "var current = new FileInfo(path);",
            "current.Refresh();",
            "!current.Exists || IsLinkedFile(current)",
            "FileAttributes.Directory",
            "FileAttributes.Device",
            "FileAttributes.ReparsePoint",
            "current.Length != preOpenLength",
            "current.LastWriteTimeUtc != preOpenModifiedUtc",
            "input.Length != preOpenLength",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.open)
        self.assertLess(self.open.index("new FileStream(path, FileMode.Open, FileAccess.Read,"),
                        self.open.index("RejectLinkedDirectories(Path.GetDirectoryName(path)!);"))
        self.assertLess(self.open.index("RejectLinkedDirectories(Path.GetDirectoryName(path)!);"),
                        self.open.index("return input;"))

    def test_failed_postopen_check_closes_handle_and_rethrows(self):
        self.assertIn("FileStream? input = null;", self.open)
        self.assertIn("catch\n        {", self.open)
        self.assertIn("input?.Dispose();", self.open)
        self.assertIn("throw;", self.open)
        self.assertLess(self.open.index("input?.Dispose();"), self.open.index("throw;"))
        self.assertNotIn("catch (IOException)", self.open)

    def test_preopen_checks_refuse_links_and_oversize_files(self):
        for marker in (
            "!file.Exists || file.Length == 0 || file.Length > MaxCopyBytes",
            "IsLinkedFile(file)",
            "long preOpenLength = file.Length;",
            "DateTime preOpenModifiedUtc = file.LastWriteTimeUtc;",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.open)
        self.assertLess(self.open.index("IsLinkedFile(file)"),
                        self.open.index("beforeOpen?.Invoke(path);"))
        self.assertIn("FileMode.Open, FileAccess.Read,", self.open)
        self.assertIn("FileShare.Read", self.open)

    def test_existing_same_handle_hash_zip_checks_remain_in_production(self):
        self.assertIn("using var input = OpenRecoveryReadStream(copy);", self.copy)
        self.assertIn("VerifyOpenedPackage(input, extension, manifest.Sha256,", self.copy)
        self.assertIn("VerifyAndHashExportAsync(", self.capture)
        self.assertIn("new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true)", self.store)
        self.assertIn("DtdProcessing = DtdProcessing.Prohibit", self.store)
        self.assertNotIn("OpenRecoveryReadStreamForTest(", self.copy)
        self.assertNotIn("OpenRecoveryReadStreamForTest(", self.capture)

    def test_tests_cover_symlink_collision_regular_rewrite_and_clean_open(self):
        for name in (
            "UnchangedRegularCopyOpensReadOnlyThroughTheGuard",
            "SymlinkInsertedAfterPreflightIsRejectedEvenWithIdenticalTargetBytes",
            "RegularFileRewrittenAfterPreflightIsRejectedWithoutReading",
            "FailedBeforeOpenHookLeavesTheOriginalUntouched",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(4, self.tests.count("[TestMethod]"))
        self.assertIn("File.CreateSymbolicLink(path, unrelated);", self.tests)
        self.assertIn("Assert.ThrowsExactly<InvalidDataException>", self.tests)

    def test_no_unverified_native_nofollow_is_claimed(self):
        for forbidden in (
            "NtCreateFile(", "openat(", "openat2(", "GetFileInformationByHandle(",
            "fstat(", "DllImport(", "LibraryImport(", "FileOptions.OpenReparsePoint",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.open)
        self.assertIn("path-based checks", self.open)

    def test_manual_os_gates_and_release_block_are_registered(self):
        step = "recovery-preopen-guard-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + step + '"'))
        self.assertIn('"-p", "test_recovery_preopen_guard_contract.py"', self.runner)
        self.assertLess(self.runner.index('CommandStep("' + step + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bc-recovery-preopen-windows-ntfs-manual-e2e",
            "gd31j27bc-recovery-preopen-astra-smb-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
