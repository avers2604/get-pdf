"""GD-31J.33 static compatibility contract: no .NET 11-only hardlink API.

Tests are intentionally source-only: no .NET restore, build, P/Invoke calls,
filesystem writes or compatibility assurances until the deferred offline lab.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
TESTS=ROOT/"tests/GetDocument.Office.Tests"

class Net10HardLinkCompatibilityContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.csproj=(ROOT/"src/GetDocument.Office/GetDocument.Office.csproj").read_text(encoding="utf-8")
        cls.adapter=(OFFICE/"EuroOfficeNativeHardLinkNoReplace.cs").read_text(encoding="utf-8")
        cls.snapshot=(OFFICE/"EuroOfficeSyntheticReviewedSnapshot.cs").read_text(encoding="utf-8")
        cls.regressions=(TESTS/"EuroOfficeNativeHardLinkNoReplaceTests.cs").read_text(encoding="utf-8")

    def test_dotnet_10_project_does_not_call_dotnet11_hardlink_api(self):
        self.assertIn("<TargetFramework>net10.0</TargetFramework>",self.csproj)
        self.assertIn("EuroOfficeNativeHardLinkNoReplace.Create(destination, pending)",self.snapshot)
        self.assertNotIn("File.CreateHardLink(destination, pending)",self.snapshot)
        self.assertNotIn("File.CreateHardLink(",self.adapter)
        self.assertNotIn("File.Move(",self.adapter)
        self.assertNotIn("File.Copy(",self.adapter)
        self.assertNotIn("File.Replace(",self.adapter)

    def test_os_native_hardlink_api_is_fail_closed_and_same_directory(self):
        for token in (
            'EntryPoint = "CreateHardLinkW"',
            'EntryPoint = "link"',
            'SetLastError = true',
            'OperatingSystem.IsWindows()',
            'OperatingSystem.IsLinux()',
            'PlatformNotSupportedException',
            'Marshal.GetLastPInvokeError()',
            'File.Exists(output) || Directory.Exists(output)',
            'if (!string.Equals(inputFolder, outputFolder, comparison))',
            'EuroOfficeSyntheticReviewedSnapshot.RequireRegularPath(',
        ):
            self.assertIn(token,self.adapter)

    def test_regressions_cover_existing_target_symlink_and_wrong_folder(self):
        for token in (
            'CreatesOnlySameDirectoryHardlinkAndRefusesToReplaceExistingDestination',
            'MissingOrSamePathAndWrongDirectoryAreRejectedBeforeNativeCall',
            'ExistingFileAndDirectoryCannotBeReplaced',
            'SourceSymlinkIsRejectedInsteadOfCreatingLinkToLink',
            'RejectsSymlinkedAncestorWithoutTouchingAnyOriginalFile',
            'DanglingDestinationSymlinkCannotBeReplaced',
        ):
            self.assertIn(token,self.regressions)

if __name__=="__main__":
    unittest.main()
