"""GD-31J.27b static contracts: lineages must not be conflated or granted rights."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class SaveAsReconciliationContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.python = (LAB / "native_saveas_protocol_reconciliation.py").read_text("utf-8")
        cls.dotnet = (OFFICE / "EuroOfficeSaveAsProtocolDiagnostic.cs").read_text("utf-8")
        cls.supervisor = (LAB / "native_ooxml_supervisor.py").read_text("utf-8")

    def test_explicit_schemas_and_strict_keys_without_version_autodowngrade(self):
        for token in (
            "getdoc-native-saveas-candidate-lab/1",
            "getdoc-native-saveas-event-lab/2",
            "getdoc-native-saveas-private-lab/1",
            "object_pairs_hook=_unique",
            "_exact(obj, LEGACY_FIELDS, LEGACY_SCHEMA)",
            "_exact(obj, CURRENT_FIELDS, CURRENT_SCHEMA)",
            "MAX_RECEIPT_BYTES = 4096",
        ):
            self.assertIn(token, self.python)
        self.assertIn("Unsupported or mixed native SaveAs version", self.python)
        self.assertIn("gd31j26-pinned/1", self.dotnet)
        self.assertIn("gd31j28-guarded/2", self.dotnet)

    def test_diagnostic_never_launches_host_or_changes_worm_or_files(self):
        for forbidden in (
            "subprocess", "os.replace(", "write_bytes(", "unlink(",
            "NativeSaveAsPublish(", "os.system(", "requests.", "socket.",
        ):
            self.assertNotIn(forbidden, self.python)
        self.assertIn('sys.dont_write_bytecode = True', self.python)
        self.assertIn('"approvedForProduction": False', self.python)
        self.assertIn('public bool ApprovedForProduction => false;', self.dotnet)
        self.assertIn('public bool NativeSaveProven => false;', self.dotnet)

    def test_existing_new_supervisor_remains_exact_six_argument_mode(self):
        self.assertIn('"--open-staged-native-saveas-lab"', self.supervisor)
        self.assertNotIn("native_saveas_protocol_reconciliation", self.supervisor)
        self.assertNotIn("native_ooxml_join", self.python)


if __name__ == "__main__":
    unittest.main()
