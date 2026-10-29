"""Offline code contracts for GD-31J.23 synthetic converter evidence.

Requires a real CEF/Qt bench for actual validation; never confers document
ready, import success, edit permission, save, PDF, network or WORM authority.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
CEF = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"

class ConversionEvidenceSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (CEF / "cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.cmake = (CEF / "CMakeLists.txt").read_text(encoding="utf-8")
        cls.probe = (OFFICE / "EuroOfficeNativeConversionProbeLab.cs").read_text(encoding="utf-8")
        cls.receipt = (OFFICE / "EuroOfficeNativeConversionCandidate.cs").read_text(encoding="utf-8")

    def test_independently_opted_in_and_synthetic_only(self):
        self.assertIn('GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB', self.cmake)
        self.assertIn('Editor.bin candidate, no edit/save or OOXML readiness" OFF)', self.cmake)
        self.assertIn('GD31J23_CONVERSION_EVIDENCE_LAB=1', self.cmake)
        self.assertIn('GD31J22_ENABLE_BINDING_LAB', self.cmake)
        self.assertIn('--open-staged-conversion-evidence-lab', self.cpp)
        self.assertIn('--open-staged-conversion-evidence-lab', self.probe)
        self.assertIn('VerifySyntheticFixture(staging, fixtureName, path, reason)', self.cpp)
        self.assertIn('EuroOfficeOsrStreamProcessLab.CreateStartInfo(', self.probe)

    def test_editor_binary_is_confined_to_private_recovery_not_user_paths(self):
        self.assertIn('editor->GetRecoveryDir()', self.cpp)
        self.assertIn('"Editor.bin"', self.cpp)
        self.assertIn('profile.path()', self.cpp)
        self.assertIn('QCryptographicHash::Sha256', self.cpp)
        self.assertIn('candidate.size() > kMaxConversionCandidateBytes', self.cpp)
        self.assertIn('bindingSeen && conversionLab', self.cpp)
        self.assertIn('conversionObserved = true', self.cpp)
        self.assertIn('evidenceSeenTwice', self.cpp)

    def test_fail_closed_diagnostic_does_not_grant_real_document(self):
        for marker in ('{"documentVerified", false}', '{"saveVerified", false}',
                       '{"pdfVerified", false}', '{"printApproved", false}',
                       '{"laboratoryOnly", true}'):
            self.assertIn(marker, self.cpp)
        self.assertIn('EditReady => false', self.receipt)
        self.assertIn('SaveCompleted => false', self.receipt)
        self.assertIn('ExpectedFields', self.receipt)
        self.assertIn('seen.Add(property.Name)', self.receipt)


if __name__ == "__main__":
    unittest.main()
