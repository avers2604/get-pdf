"""GD-31J.27 source-only checks: correlated native and OOXML evidence cannot grant WORM/edit permissions."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class NativeOoxmlJoinContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.verifier = (LAB / "native_ooxml_join.py").read_text(encoding="utf-8")
        cls.native = (LAB / "cef_staged_open.cpp").read_text(encoding="utf-8")
        cls.independent = (LAB / "synthetic_roundtrip.py").read_text(encoding="utf-8")
        cls.csharp = (OFFICE / "EuroOfficeNativeOoxmlJoinCandidate.cs").read_text(encoding="utf-8")

    def test_join_uses_real_independent_byte_verifier_not_native_flags_only(self):
        self.assertIn('ooxml.verify(staging, fixture_name)', self.verifier)
        self.assertIn('ooxml._read_limited(output_path, ooxml.MAX_FILE)', self.verifier)
        self.assertIn('ooxml._read_limited(source_path, ooxml.MAX_FILE)', self.verifier)
        self.assertIn('independent["outputSha256"] != native_sha', self.verifier)
        self.assertIn('hashlib.sha256(', self.verifier)
        self.assertIn('_read_ooxml(output, Path(fixture_name).suffix, CANARIES[fixture_name])',
                      self.independent)

    def test_untrusted_json_is_one_line_exact_schema_no_path_or_saved_file(self):
        self.assertIn('NATIVE_FIELDS = frozenset', self.verifier)
        self.assertIn('object_pairs_hook=_unique_object', self.verifier)
        self.assertIn('data.count(b"\\n") != 1', self.verifier)
        self.assertIn('sys.stdin.buffer', self.verifier)
        self.assertNotIn('subprocess', self.verifier)
        self.assertNotIn('open(', self.verifier.replace('fullmatch(', ''))
        self.assertNotIn('write_bytes(', self.verifier)
        self.assertNotIn('os.replace(', self.verifier)
        self.assertNotIn('sys.path.append(', self.verifier)

    def test_native_source_still_untrusted_and_no_process_enrollment(self):
        self.assertIn('"getdoc-native-saveas-candidate-lab/1"', self.native)
        self.assertIn('{"nativeSaveProven", false}', self.native)
        self.assertIn('{"approvedForProduction", false}', self.native)
        self.assertIn('NativeSaveProven => false', self.csharp)
        self.assertIn('ApprovedForProduction => false', self.csharp)
        self.assertIn('SyntheticCanaryVerified => true', self.csharp)
        self.assertIn('expectedSession == expectedTab', self.csharp)


if __name__ == "__main__":
    unittest.main()
