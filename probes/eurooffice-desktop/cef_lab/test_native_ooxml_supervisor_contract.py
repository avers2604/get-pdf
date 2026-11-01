"""GD-31J.27a source-only guards for optional offline supervisor; tests deferred."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class NativeSupervisorSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.supervisor = (LAB / "native_ooxml_supervisor.py").read_text(encoding="utf-8")
        cls.join = (LAB / "native_ooxml_join.py").read_text(encoding="utf-8")
        cls.parser = (OFFICE / "EuroOfficeSupervisedNativeOoxmlCandidate.cs").read_text(encoding="utf-8")

    def test_child_is_exact_pinned_saveas_and_never_a_shell_or_network(self):
        self.assertIn('"--open-staged-native-saveas-lab"', self.supervisor)
        self.assertIn('before != expected_native_sha256', self.supervisor)
        self.assertIn('before != after', self.supervisor)
        self.assertIn('subprocess.Popen(', self.supervisor)
        self.assertIn('shell=False', self.supervisor)
        self.assertIn('stderr=subprocess.DEVNULL', self.supervisor)
        self.assertIn('join.MAX_NATIVE_BYTES + 1', self.supervisor)
        self.assertIn('subprocess.DEVNULL', self.supervisor)
        self.assertNotIn('shell=True', self.supervisor)
        self.assertNotIn('requests.', self.supervisor)
        self.assertNotIn('os.system(', self.supervisor)
        self.assertNotIn('write_bytes(', self.supervisor)

    def test_disposable_gate_bytecode_and_denied_privileges(self):
        self.assertIn('approved_disposable_vm', self.supervisor)
        self.assertIn('OUTPUT_MARKER', self.supervisor)
        self.assertIn('os.path.lexists(output_file)', self.supervisor)
        self.assertIn('sys.dont_write_bytecode = True', self.supervisor)
        self.assertIn('sys.dont_write_bytecode = True', self.join)
        self.assertIn('join.correlate_from_stream(', self.supervisor)
        self.assertIn('"nativeSaveProven": False', self.supervisor)
        self.assertIn('"approvedForProduction": False', self.supervisor)

    def test_nested_dotnet_parser_cannot_grant_production_rights(self):
        self.assertIn('EuroOfficeNativeOoxmlJoinCandidate.Parse(', self.parser)
        self.assertIn('Unknown or duplicate supervised candidate field.', self.parser)
        self.assertIn('public bool NativeSaveProven => false;', self.parser)
        self.assertIn('public bool ApprovedForProduction => false;', self.parser)
        self.assertNotIn('File.WriteAllBytes', self.parser)


if __name__ == "__main__":
    unittest.main()
