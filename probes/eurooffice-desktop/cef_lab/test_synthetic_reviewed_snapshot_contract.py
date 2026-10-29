"""GD-31J.29 offline source-level contract for reviewed synthetic snapshots.

No Qt process, filesystem mutation or build is run by these source tests.
They do not prove power-loss durability, secure shared-volume isolation or
production publication readiness.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
LAB=ROOT/"probes/eurooffice-desktop/cef_lab"

class ReviewedSyntheticSnapshotSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source=(OFFICE/"EuroOfficeSyntheticReviewedSnapshot.cs").read_text(encoding="utf-8")
        cls.runner=(OFFICE/"EuroOfficeNativeSaveAsLab.cs").read_text(encoding="utf-8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text(encoding="utf-8")

    def test_restricted_to_reviewed_synthetic_path_and_manifest(self):
        for phrase in (
            'getdoc', 'gd31j6-fixture-manifest.json',
            'gd31j6-office-fixtures/1',
            'test-only-local-ooxml-no-corporate-documents',
            'Path.Combine(root, "reviewed")',
            'gd31j29-reviewed-',
            'native.FailureCode != "candidate_untrusted"',
            'native.Phase != "quarantined"',
            '!native.CorrelatedNativeCallbacks',
            'RequireRegularPath(reviewed, expectDirectory: true)',
            'EuroOfficeNativeArtifactCandidate.InspectRevision(',
            'FileMode.CreateNew',
        ):
            self.assertIn(phrase,self.source)
        self.assertIn('GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB',self.cmake)

    def test_flush_then_atomic_no_replace_hardlink_without_move_fallback(self):
        self.assertIn('FileOptions.WriteThrough',self.source)
        self.assertIn('output.Flush(flushToDisk: true)',self.source)
        self.assertIn('HashBounded(pending, out long pendingLength)',self.source)
        self.assertIn('File.CreateHardLink(destination, pending)',self.source)
        self.assertIn('File.Delete(pending)',self.source)
        self.assertNotIn('File.Move(',self.source)
        self.assertNotIn('File.Copy(',self.source)
        self.assertNotIn('File.Replace(',self.source)
        self.assertNotIn('Directory.CreateDirectory(',self.source)

    def test_bounded_against_filesystem_links_and_no_prod_rights(self):
        self.assertIn('FileAttributes.ReparsePoint',self.source)
        self.assertIn('File.Exists(destination) || Directory.Exists(destination)',self.source)
        self.assertIn('AuthorizedForPublication => false',self.source)
        self.assertIn('WormUpdated => false',self.source)
        self.assertIn('SaveCompleted => false',self.source)
        self.assertIn('DurableAcrossPowerLoss => false',self.source)
        self.assertIn('labApproved = false',self.source)
        self.assertIn('EuroOfficeNativeSaveAsEventReceipt.Parse(',self.runner)

if __name__=="__main__":
    unittest.main()
