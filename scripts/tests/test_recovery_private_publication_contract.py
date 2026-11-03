"""GD-31J.27bg: source-only contracts for private, non-overwriting publication.

Source string inspection only. NOT C# compilation, runtime filesystem/OS
security tests, or proof of crash-durable atomic replacement.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
PUBLISHER = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryPrivateCopyPublisher.cs"
STAGER = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryVerifiedStreamStage.cs"
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
ADAPTERS = ROOT / "src/GetDocument.Office/LibreOffice/RecoveryNativeIdentityAdapters.cs"
TESTS = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryPrivatePublicationTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class PrivateRecoveryPublicationSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.publisher = PUBLISHER.read_text(encoding="utf-8")
        cls.stager = STAGER.read_text(encoding="utf-8")
        cls.store = STORE.read_text(encoding="utf-8")
        cls.adapters = ADAPTERS.read_text(encoding="utf-8")
        cls.tests = TESTS.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.publish = cls.publisher.split(
            "internal static async Task<RecoveryPrivatePublication> PublishAsync(", 1
        )[1].split("private static void RequireOwnedRegularStaging(", 1)[0]
        cls.cleanup = cls.publisher.split(
            "internal static void TryDeleteOwnRegularFile(", 1
        )[1].split("internal static void TryRemoveEmptyPrivateDirectory(", 1)[0]

    def test_publication_uses_generated_private_directory_not_user_path(self):
        self.assertIn('Directory.CreateTempSubdirectory("getdoc-recovery-private-")',
                      self.publish)
        self.assertIn('string name = Guid.NewGuid().ToString("N");', self.publish)
        self.assertIn('name + ".part" + extension', self.publish)
        self.assertIn('name + extension', self.publish)
        self.assertNotIn("string destinationPath", self.publish)
        self.assertIn("RejectLinkedDirectoryTree(root);", self.publish)

    def test_stage_uses_create_new_and_no_existing_destination_overwrite(self):
        self.assertIn("FileMode.CreateNew", self.publish)
        self.assertIn("FileAccess.ReadWrite, FileShare.Delete", self.publish)
        self.assertIn("File.Move(stagingPath, destination, overwrite: false);",
                      self.publish)
        self.assertNotIn("overwrite: true", self.publish)
        self.assertNotIn("File.Copy(", self.publish)
        self.assertNotIn("File.Replace(", self.publisher)
        self.assertNotIn("Directory.Delete(directory, recursive: true)", self.publisher)

    def test_only_held_verified_source_session_is_consumed(self):
        self.assertIn("RecoveryVerifiedStreamStage session", self.publish)
        self.assertIn("using (session)", self.publish)
        self.assertIn("await session.StageToAsync(staging, expectedSha256,", self.publish)
        self.assertIn("OfficeRecoveryStore.VerifyOpenedPackage(", self.publish)
        self.assertNotIn("RecoveryNativeIdentityGate.OpenVerifiedRead(", self.publish)
        self.assertNotIn("new FileStream(sourcePath", self.publish)
        self.assertNotIn("OfficeRecoveryStore.CaptureAsync(", self.publisher)

    def test_full_digest_zip_and_disk_flush_precede_publish(self):
        self.assertIn("staging.Flush(flushToDisk: true);", self.publish)
        self.assertIn("RequireOwnedRegularStaging(stagingPath, staging, receipt);",
                      self.publish)
        self.assertIn("staging, extension, receipt.Sha256", self.publish)
        self.assertLess(self.publish.index("Flush(flushToDisk: true)"),
                        self.publish.index("File.Move(stagingPath, destination"))
        self.assertLess(self.publish.index("OfficeRecoveryStore.VerifyOpenedPackage("),
                        self.publish.index("File.Move(stagingPath, destination"))
        self.assertIn("renamedStage = true;", self.publish)
        self.assertIn("new RecoveryPrivatePublication(", self.publish)
        self.assertIn("staging = null; // owner now controls the held descriptor", self.publish)

    def test_collision_cleanup_preserves_foreign_destination(self):
        self.assertIn("if (renamedStage)", self.publish)
        self.assertIn("else if (createdStage)", self.publish)
        self.assertIn("TryDeleteOwnRegularFile(root, stagingPath);", self.publish)
        self.assertIn("TryDeleteOwnRegularFile(root, destination);", self.publish)
        self.assertNotIn("if (File.Exists(destination)) File.Delete(destination)", self.publish)
        self.assertIn("info.LinkTarget is not null", self.cleanup)
        self.assertIn("Path.GetDirectoryName(Path.GetFullPath(filePath))", self.cleanup)
        self.assertIn("File.Delete(filePath);", self.cleanup)
        self.assertIn("Directory.Delete(directory, recursive: false);", self.publisher)

    def test_disposal_only_releases_held_stream_and_owned_private_entry(self):
        self.assertIn("internal sealed class RecoveryPrivatePublication : IDisposable",
                      self.publisher)
        self.assertIn("Interlocked.Exchange(ref _heldStream, null)", self.publisher)
        self.assertIn("handle.Dispose();", self.publisher)
        self.assertIn("TryDeleteOwnRegularFile(", self.publisher)
        self.assertIn("TryRemoveEmptyPrivateDirectory(", self.publisher)
        self.assertIn("internal RecoveryStagedCopyReceipt Receipt", self.publisher)
        self.assertNotIn("Worm", self.publisher)
        self.assertNotIn("FilePath = sourcePath", self.publisher)

    def test_synthetic_mstest_covers_collision_cancellation_and_unrelated_files(self):
        names = (
            "ValidStageIsPublishedWithoutLeavingPartFileAndOwnedUntilDispose",
            "WrongDigestDiscardsOnlyOwnedUnpublishedStage",
            "ExistingDestinationCollisionMustNotReplaceForeignContents",
            "CancellationBeforeRenameNeverPublishesFile",
            "ExistingSymbolicLinkAtDestinationCannotBeOverwritten",
            "InvalidExtensionDoesNotCreatePrivateDirectory",
            "DisposingPublicationCannotDeleteUnrelatedNewFiles",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(7, self.tests.count("[TestMethod]"))
        self.assertIn("new FakeAdapter(", self.tests)

    def test_production_remains_unwired_and_manual_release_stays_blocked(self):
        for forbidden in (
            "RecoveryPrivateCopyPublisher.PublishAsync(",
            "RecoveryPrivatePublication",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.store)
        self.assertEqual(2, self.adapters.count(
            "public bool SupportsVerifiedDirectoryRelativeOpen => false;"))
        name = "recovery-private-publication-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + name + '"'))
        self.assertIn('"-p", "test_recovery_private_publication_contract.py"',
                      self.runner)
        self.assertLess(self.runner.index('CommandStep("' + name + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for manual in (
            "gd31j27bg-private-publication-windows-ntfs-manual-e2e",
            "gd31j27bg-private-publication-astra-ext4-manual-e2e",
            "gd31j27bg-private-publication-smb-manual-e2e",
        ):
            self.assertIn(manual, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
