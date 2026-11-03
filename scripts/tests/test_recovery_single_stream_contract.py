"""GD-31J.27bb source-only checks for single-handle SHA + ZIP recovery validation.

Text checks, not a .NET build, OS permission test, or performance measurement.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoverySingleStreamTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoverySingleStreamSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.capture = cls.store.split("public async Task<OfficeRecoveryCopy> CaptureAsync(", 1)[1].split(
            "public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1
        )[0]
        cls.copy = cls.store.split("private OfficeRecoveryCopy? ReadValidCopy(", 1)[1].split(
            "private void Prune(", 1
        )[0]
        cls.zip = cls.store.split("private static void EnsureValidPackage(", 1)[1].split(
            "private static void ValidateEntryContents(", 1
        )[0]
        cls.open = cls.store.split("private static FileStream OpenRecoveryReadStream(", 1)[1].split(
            "internal static bool VerifyOpenedPackage(", 1
        )[0]
        cls.verify = cls.store.split("internal static bool VerifyOpenedPackage(", 1)[1].split(
            "private static async Task<string> VerifyAndHashExportAsync(", 1
        )[0]
        cls.export = cls.store.split("private static async Task<string> VerifyAndHashExportAsync(", 1)[1].split(
            "internal static bool DeleteVerifiedPair(", 1
        )[0]

    def test_capture_zip_and_async_hash_share_export_handle(self):
        self.assertIn("VerifyAndHashExportAsync(", self.capture)
        self.assertNotIn("HashFileAsync(temporaryCopy", self.capture)
        self.assertIn("await using var input = OpenRecoveryReadStream(path, FileOptions.Asynchronous);",
                      self.export)
        self.assertIn("EnsureValidPackage(input, extension);", self.export)
        self.assertIn("SHA256.HashDataAsync(input, cancellationToken)", self.export)
        self.assertLess(self.export.index("EnsureValidPackage(input, extension);"),
                        self.export.index("SHA256.HashDataAsync(input, cancellationToken)"))
        self.assertIn("cancellationToken.ThrowIfCancellationRequested();", self.export)

    def test_sha_then_zip_share_one_read_stream_for_recovery_lookup(self):
        self.assertIn("using var input = OpenRecoveryReadStream(copy);", self.copy)
        self.assertIn("VerifyOpenedPackage(input, extension, manifest.Sha256,", self.copy)
        self.assertNotIn("HashFile(copy)", self.copy)
        self.assertNotIn("EnsureValidPackage(copy, extension)", self.copy)
        self.assertIn("SHA256.HashData(input)", self.verify)
        self.assertIn("EnsureValidPackage(input, extension);", self.verify)
        self.assertLess(self.verify.index("SHA256.HashData(input)"),
                        self.verify.index("EnsureValidPackage(input, extension);"))
        self.assertIn("input.Position = 0;", self.verify)

    def test_opening_is_read_only_and_denies_cooperative_writers(self):
        self.assertIn("new FileStream(path, FileMode.Open, FileAccess.Read,", self.open)
        self.assertIn("FileShare.Read", self.open)
        self.assertIn("FileOptions options = FileOptions.None", self.open)
        self.assertNotIn("FileShare.ReadWrite", self.open)
        self.assertNotIn("FileMode.Create", self.open)
        self.assertIn("!file.Exists || file.Length == 0 || file.Length > MaxCopyBytes", self.open)
        self.assertIn("IsLinkedFile(file)", self.open)
        self.assertIn("input.Length > MaxCopyBytes", self.open)

    def test_zip_archive_is_disposed_without_closing_caller_stream(self):
        self.assertIn("private static void EnsureValidPackage(Stream input, string extension)",
                      self.store)
        self.assertIn("input.CanRead", self.zip)
        self.assertIn("input.CanSeek", self.zip)
        self.assertIn("input.Position = 0;", self.zip)
        self.assertIn("new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true)", self.zip)
        self.assertNotIn("ZipFile.OpenRead(", self.zip)
        self.assertIn("ValidateEntryContents(entry);", self.zip)
        self.assertIn("MaxEntryCount", self.zip)
        self.assertIn("MaxExpandedBytes", self.zip)

    def test_full_zip_validation_remains_mandatory_after_sha_match(self):
        self.assertIn("string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase)",
                      self.verify)
        self.assertIn("if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))",
                      self.verify)
        self.assertIn("afterHash?.Invoke();", self.verify)
        self.assertIn("EnsureValidPackage(input, extension);", self.verify)
        self.assertLess(self.verify.index("afterHash?.Invoke();"),
                        self.verify.index("EnsureValidPackage(input, extension);"))
        self.assertIn("return true;", self.verify)
        self.assertIn("DtdProcessing = DtdProcessing.Prohibit", self.store)

    def test_copy_still_checks_before_and_after_metadata_and_link_status(self):
        for marker in (
            "var info = new FileInfo(copy);",
            "IsLinkedFile(info)",
            "long beforeLength = info.Length;",
            "DateTime beforeModifiedUtc = info.LastWriteTimeUtc;",
            "if (input.Length != beforeLength) return null;",
            "var after = new FileInfo(copy);",
            "IsLinkedFile(after)",
            "after.Length != beforeLength",
            "after.LastWriteTimeUtc != beforeModifiedUtc",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.copy)
        self.assertIn("afterHash is null ? null : () => afterHash(copy)", self.copy)

    def test_synthetic_mstest_has_hash_zip_stream_and_mutation_regressions(self):
        for case in (
            "SameSeekableStreamVerifiesHashAndZipAndRemainsOpen",
            "WrongHashDoesNotOpenOrTrustInvalidZip",
            "CorrectHashWithoutRequiredOfficePartIsStillInvalid",
            "NonSeekableStreamIsRejectedBeforeHashAndZip",
            "CorruptedZipAfterHashFailsRatherThanAcceptingOldDigest",
        ):
            with self.subTest(case=case):
                self.assertIn(case, self.tests)
        self.assertEqual(5, self.tests.count("[TestMethod]"))
        self.assertIn("Assert.IsTrue(input.CanRead", self.tests)
        self.assertIn("input.SetLength(8);", self.tests)

    def test_manual_acceptance_and_release_block_are_preserved(self):
        name = "recovery-single-stream-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
        self.assertIn('"-p", "test_recovery_single_stream_contract.py"', self.runner)
        self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27bb-single-stream-windows-ntfs-manual-e2e",
            "gd31j27bb-single-stream-astra-smb-manual-e2e",
        ):
            self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
