"""GD-31J.27az: source-only regression guard for bounded verified recovery lookup.

These checks only inspect source text. They do not execute C#, ZIP, SMB, or MSTest.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryFastSelectionTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryFastSelectionSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = STORE.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.find = cls.store.split("internal OfficeRecoveryCopy? FindLatestWithEnumerator(", 1)[1].split(
            "public void Discard(", 1
        )[0]
        cls.copy = cls.store.split("private OfficeRecoveryCopy? ReadValidCopy(", 1)[1].split(
            "private void Prune(", 1
        )[0]

    def test_complete_enumeration_precedes_candidate_verification(self):
        self.assertIn("enumerateManifests(folder)", self.find)
        self.assertIn("Take(MaxManifestCandidates + 1).ToArray();", self.find)
        self.assertIn("if (manifestPaths.Length > MaxManifestCandidates)", self.find)
        self.assertIn("throw new InvalidDataException(", self.find)
        self.assertLess(self.find.index("ToArray();"), self.find.index("ReadManifest(path)"))
        self.assertLess(self.find.index("ToArray();"), self.find.index("ReadValidCopy(source, extension, entry.Path)"))

    def test_latest_first_sort_then_full_verify_until_first_valid(self):
        for marker in (
            'Guid.TryParseExact(',
            'Path.GetFileNameWithoutExtension(path), "N", out _)',
            'string.Equals(entry.Metadata.SourcePath, source, PathComparison)',
            "entry.Metadata.CapturedUtc <= latestAllowed",
            "OrderByDescending(entry => entry.Metadata!.CapturedUtc)",
            "ReadValidCopy(source, extension, entry.Path)",
            "if (candidate is not null)",
            "return candidate;",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.find)
        self.assertLess(self.find.index("OrderByDescending("),
                        self.find.index("ReadValidCopy(source, extension, entry.Path)"))
        self.assertLess(self.find.index("ReadValidCopy(source, extension, entry.Path)"),
                        self.find.index("if (candidate is not null)"))

    def test_untrusted_manifest_sort_does_not_replace_verification(self):
        self.assertIn("Metadata = ReadManifest(path)", self.find)
        self.assertIn("verifyCandidate is null", self.find)
        self.assertIn("ReadValidCopy(source, extension, entry.Path)", self.find)
        self.assertIn("var manifest = ReadManifest(manifestPath);", self.copy)
        self.assertIn("string.Equals(manifest.SourcePath, source, PathComparison)", self.copy)
        self.assertIn("if (!string.Equals(sha, manifest.Sha256, StringComparison.OrdinalIgnoreCase))", self.copy)
        self.assertIn("EnsureValidPackage(copy, extension);", self.copy)

    def test_sha_fast_rejection_preserves_link_and_size_guards(self):
        expected = (
            "var info = new FileInfo(copy);",
            "!info.Exists || info.Length == 0 || info.Length > MaxCopyBytes",
            "IsLinkedFile(info)",
            "string sha = HashFile(copy);",
            "EnsureValidPackage(copy, extension);",
        )
        for marker in expected:
            with self.subTest(marker=marker):
                self.assertIn(marker, self.copy)
        self.assertLess(self.copy.index("IsLinkedFile(info)"),
                        self.copy.index("string sha = HashFile(copy);"))
        self.assertLess(self.copy.index("string sha = HashFile(copy);"),
                        self.copy.index("EnsureValidPackage(copy, extension);"))
        self.assertLess(self.copy.index("if (!string.Equals(sha, manifest.Sha256"),
                        self.copy.index("EnsureValidPackage(copy, extension);"))
        self.assertNotIn("static readonly Dictionary<", self.copy)

    def test_copy_size_and_manifest_counts_are_bounded(self):
        self.assertIn("private const int MaxManifestCandidates = 4096;", self.store)
        self.assertIn("private const long MaxCopyBytes = 128L * 1024 * 1024;", self.store)
        self.assertIn("private const int RetainedCopies = 3;", self.store)
        self.assertIn("if (manifestPaths.Length > MaxManifestCandidates)", self.find)
        self.assertNotIn("return null; // too many", self.find)

    def test_csharp_synthetic_regressions_cover_early_exit_fallback_and_limit(self):
        for name in (
            "NewestValidSnapshotIsTheOnlyOneFullyVerified",
            "FailedNewestVerificationFallsBackToOlderVerifiedCopy",
            "TooManyManifestsFailsClosedBeforeReadingAnySnapshot",
        ):
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(7, self.tests.count("[TestMethod]"))
        self.assertIn("FindLatestWithEnumerator(", self.tests)
        self.assertIn("Assert.ThrowsExactly<InvalidDataException>", self.tests)

    def test_no_new_file_mutation_or_automatic_recovery(self):
        for forbidden in ("File.Delete(", "File.Move(", "Directory.Delete(",
                          "Process.Start(", "CaptureAsync(", "Discard("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.find)
        self.assertIn("ReadValidCopy(source, extension, entry.Path)", self.find)

    def test_manual_performance_acceptance_is_not_claimed(self):
        self.assertEqual(1, self.runner.count('CommandStep("recovery-fast-selection-source-tests"'))
        self.assertIn('"-p", "test_recovery_fast_selection_contract.py"', self.runner)
        self.assertLess(self.runner.index('CommandStep("recovery-fast-selection-source-tests"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for gate in (
            "gd31j27az-recovery-fast-selection-windows-ntfs-manual-e2e",
            "gd31j27az-recovery-fast-selection-astra-smb-manual-e2e",
        ):
            with self.subTest(gate=gate):
                self.assertIn(gate, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
