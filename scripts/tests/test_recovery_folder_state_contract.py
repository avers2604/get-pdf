"""GD-31J.27ay: source-only guards for verified-empty vs unreadable recovery folders.

This file does not run C#, exercise Windows/Astra ACLs, or certify an SMB path.
"""
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
STORE = ROOT / "src/GetDocument.Office/LibreOffice/OfficeRecoveryStore.cs"
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"
C_SHARP_TEST = ROOT / "tests/GetDocument.Office.Tests/OfficeRecoveryFolderStateTests.cs"
RUNNER = ROOT / "scripts/office_acceptance.py"


class RecoveryFolderStateSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = STORE.read_text(encoding="utf-8")
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.tests = C_SHARP_TEST.read_text(encoding="utf-8")
        cls.runner = RUNNER.read_text(encoding="utf-8")
        cls.inspect = cls.store.split(
            "public OfficeRecoveryIncompleteInventory InspectIncomplete(", 1
        )[1].split("private static bool TryClassifyIncompleteName(", 1)[0]
        cls.find = cls.store.split("public OfficeRecoveryCopy? FindLatest(", 1)[1].split(
            "public void Discard(", 1
        )[0]
        cls.discard = cls.store.split("public void Discard(", 1)[1].split(
            "private OfficeRecoveryCopy? ReadValidCopy(", 1
        )[0]
        cls.probe = cls.store.split("internal static bool RecoveryFolderExists(", 1)[1].split(
            "private string FolderFor(", 1
        )[0]

    def test_inspection_empty_requires_confirmed_missing_directory(self):
        self.assertIn("if (!RecoveryFolderExists(folder))", self.inspect)
        self.assertNotIn("if (!Directory.Exists(folder))", self.inspect)
        self.assertIn("InspectIncompleteWithEnumerator(", self.inspect)
        self.assertIn("foreach (string entry in enumerateEntries(folder))", self.inspect)
        self.assertIn("SearchOption.TopDirectoryOnly", self.inspect)

    def test_find_latest_empty_requires_confirmed_missing_directory(self):
        self.assertIn("if (!RecoveryFolderExists(folder)) return null;", self.find)
        self.assertNotIn("if (!Directory.Exists(folder)) return null;", self.find)
        self.assertIn("FindLatestWithEnumerator(", self.find)
        self.assertIn("foreach (string manifestPath in enumerateManifests(folder))", self.find)
        self.assertIn("RejectLinkedDirectories(folder);", self.find)

    def test_discard_fails_closed_instead_of_dropping_inaccessible_folder(self):
        self.assertIn("if (!RecoveryFolderExists(folder)) return;", self.discard)
        self.assertNotIn("if (!Directory.Exists(folder)) return;", self.discard)
        self.assertIn("ReadValidCopy(source, extension, manifest)", self.discard)
        self.assertIn("DeleteVerifiedPair(verified.FilePath, manifest, TryDeleteVerifiedPart)",
                      self.discard)

    def test_probe_only_catches_missing_and_rejects_non_directories(self):
        self.assertIn("File.GetAttributes", self.probe)
        self.assertIn("catch (FileNotFoundException)", self.probe)
        self.assertIn("catch (DirectoryNotFoundException)", self.probe)
        self.assertIn("FileAttributes.Directory", self.probe)
        self.assertIn("FileAttributes.ReparsePoint", self.probe)
        self.assertIn("throw new InvalidDataException(", self.probe)
        for forbidden in ("Directory.Exists(", "catch (IOException)", "catch (Exception)",
                          "UnauthorizedAccessException", "SecurityException"):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.probe)

    def test_late_enumeration_errors_are_not_suppressed_by_broad_catch(self):
        for name, method in (("inspect", self.inspect), ("find", self.find)):
            with self.subTest(name=name):
                self.assertNotIn("catch (DirectoryNotFoundException)", method)
                self.assertNotIn("catch (IOException)", method)
                self.assertNotIn("catch (UnauthorizedAccessException)", method)
        self.assertIn("DirectoryNotFoundException", self.tests)
        self.assertIn("VanishingEntries(", self.tests)
        self.assertIn("yield return Path.Combine(", self.tests)

    def test_synthetic_mstest_covers_status_distinctions_and_permission_faults(self):
        required = (
            "MissingRecoveryFolderMeansEmptyOnlyWhenActuallyAbsent",
            "VerifiedEmptyFolderMeansNoSnapshotButCreatesNothing",
            "OnlyExplicitMissingErrorsAreAllowedToMeanNoFolder",
            "AccessDeniedAndSecurityErrorCannotMasqueradeAsEmpty",
            "NonDirectoryAndReparseEntriesCannotMasqueradeAsEmpty",
            "FindLatestFailsClosedWhenFolderDisappearsDuringLazyEnumeration",
            "InspectFailsClosedWhenFolderDisappearsDuringLazyEnumeration",
            "DirectoryEnumerationAccessDenialNeverBecomesCleanInventory",
        )
        for name in required:
            with self.subTest(name=name):
                self.assertIn(name, self.tests)
        self.assertEqual(8, self.tests.count("[TestMethod]"))

    def test_ui_already_shows_unknown_state_as_failure_not_clean_empty(self):
        self.assertIn('Text = "Не удалось проверить временные файлы"', self.pane)
        self.assertIn("ex is IOException or InvalidDataException", self.pane)
        self.assertIn("UnauthorizedAccessException", self.pane)
        self.assertIn("System.Security.SecurityException", self.pane)
        self.assertIn("Ничего не удалено и не восстановлено", self.pane)

    def test_offline_runner_registration_is_unique_and_release_gated(self):
        step = "recovery-folder-state-source-tests"
        self.assertEqual(1, self.runner.count('CommandStep("' + step + '"'))
        self.assertIn('"-p", "test_recovery_folder_state_contract.py"', self.runner)
        self.assertLess(self.runner.index('CommandStep("' + step + '"'),
                        self.runner.index('CommandStep("solution-restore"'))
        for manual in (
            "gd31j27ay-recovery-folder-state-windows-ntfs-manual-e2e",
            "gd31j27ay-recovery-folder-state-astra-smb-manual-e2e",
        ):
            self.assertIn(manual, self.runner)
        self.assertIn('"releaseReady": False', self.runner)


if __name__ == "__main__":
    unittest.main()
