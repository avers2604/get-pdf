"""GD-31J.27ba source-only race guards. Not executable .NET or OS test evidence."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryFastSelectionTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class ConcurrentRecoverySelectionSourceContract(unittest.TestCase):
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
        cls.verify = cls.store.split("internal static bool VerifyOpenedPackage(", 1)[1].split(
            "private static async Task<string> VerifyAndHashExportAsync(", 1
        )[0]

    def test_snapshot_is_materialized_before_candidate_sorting(self):
        self.assertIn("var observations = manifestPaths", self.find)
        self.assertIn("Metadata = ReadManifest(path)", self.find)
        self.assertIn(".ToArray();", self.find)
        self.assertIn("var ordered = observations", self.find)
        self.assertLess(self.find.index("var observations = manifestPaths"),
                        self.find.index("var ordered = observations"))
        self.assertIn("OrderByDescending(entry => entry.Metadata!.CapturedUtc)", self.find)

    def test_manifest_set_is_checked_again_before_return(self):
        for marker in (
            "void EnsureSurveyStillValid()",
            "string[] currentPaths = enumerateManifests(folder)",
            "currentPaths.Length != manifestPaths.Length",
            "new HashSet<string>(manifestPaths, comparer).SetEquals(currentPaths)",
            "throw new InvalidDataException(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.find)
        self.assertIn("StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal", self.find)
        self.assertNotIn("Directory.Delete(", self.find)

    def test_manifest_metadata_is_read_again_after_verification(self):
        self.assertIn("Equals(observation.Metadata, ReadManifest(observation.Path))",
                      self.find)
        self.assertIn("candidate.CapturedUtc != entry.Metadata!.CapturedUtc", self.find)
        self.assertIn("if (candidate is not null)", self.find)
        self.assertIn("EnsureSurveyStillValid();", self.find)
        self.assertLess(self.find.index("ReadValidCopy(source, extension, entry.Path)"),
                        self.find.index("EnsureSurveyStillValid();\n                return candidate;"))
        self.assertIn("EnsureSurveyStillValid();\n        return null;", self.find)

    def test_copy_identity_is_checked_again_after_full_zip_verification(self):
        for marker in (
            "long beforeLength = info.Length;",
            "DateTime beforeModifiedUtc = info.LastWriteTimeUtc;",
            "using var input = OpenRecoveryReadStream(copy);",
            "VerifyOpenedPackage(input, extension, manifest.Sha256,",
            "var after = new FileInfo(copy);",
            "after.Length != beforeLength",
            "after.LastWriteTimeUtc != beforeModifiedUtc",
            "IsLinkedFile(after)",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.copy)
        self.assertLess(self.copy.index("using var input = OpenRecoveryReadStream(copy);"),
                        self.copy.index("VerifyOpenedPackage(input, extension, manifest.Sha256,"))
        self.assertLess(self.copy.index("VerifyOpenedPackage(input, extension, manifest.Sha256,"),
                        self.copy.index("after.Length != beforeLength"))
        self.assertLess(self.verify.index("SHA256.HashData(input)"),
                        self.verify.index("EnsureValidPackage(input, extension);"))

    def test_test_only_posthash_callback_cannot_replace_production_validator(self):
        self.assertIn("internal OfficeRecoveryCopy? ReadValidCopyWithPostHashMutation(",
                      self.store)
        self.assertIn("Action<string>? afterHash = null", self.store)
        self.assertIn("afterHash is null ? null : () => afterHash(copy)", self.copy)
        self.assertIn("afterHash?.Invoke();", self.verify)
        self.assertLess(self.verify.index("SHA256.HashData(input)"),
                        self.verify.index("afterHash?.Invoke();"))
        self.assertLess(self.verify.index("afterHash?.Invoke();"),
                        self.verify.index("EnsureValidPackage(input, extension);"))
        self.assertIn("verifyCandidate is null", self.find)
        self.assertIn("ReadValidCopy(source, extension, entry.Path)", self.find)
        self.assertNotIn("ReadValidCopyWithPostHashMutation(", self.find)

    def test_synthetic_race_regressions_cover_manifest_and_file_edits(self):
        for case in (
            "ChangedManifestTimestampDuringValidationFailsClosed",
            "InvalidManifestBecomesValidDuringVerificationFailsClosed",
            "NewManifestAddedDuringVerificationFailsClosed",
            "CopyChangedAfterHashMustNotBeAccepted",
        ):
            with self.subTest(case=case):
                self.assertIn(case, self.tests)
        self.assertEqual(7, self.tests.count("[TestMethod]"))
        self.assertIn("Assert.ThrowsExactly<InvalidDataException>", self.tests)
        self.assertIn("File.AppendAllText(path, \"SYNTHETIC_AFTER_HASH\")", self.tests)

    def test_new_selection_path_stays_read_only(self):
        for forbidden in ("File.Delete(", "File.Move(", "Directory.Delete(",
                          "File.Write", "CaptureAsync(", "Discard("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.find)
                self.assertNotIn(forbidden, self.copy)

    def test_runner_registration_keeps_manual_gates_not_run(self):
        name = "recovery-concurrent-selection-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
        self.assertIn('"-p", "test_recovery_concurrent_selection_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for manual in (
            "gd31j27ba-recovery-concurrent-windows-ntfs-manual-e2e",
            "gd31j27ba-recovery-concurrent-astra-smb-manual-e2e",
        ):
            self.assertIn(manual, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
