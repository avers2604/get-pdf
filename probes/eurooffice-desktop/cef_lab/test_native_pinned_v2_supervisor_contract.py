"""GD-31J.27d source-only contracts; deferred until offline acceptance."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / "probes/eurooffice-desktop/cef_lab"
DOTNET = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"


class SupervisedPinnedV2Contracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.controller = (LAB / "native_pinned_v2_supervisor.py").read_text("utf-8")
        cls.v1 = (LAB / "native_ooxml_supervisor.py").read_text("utf-8")
        cls.join = (LAB / "native_pinned_event_ooxml_join.py").read_text("utf-8")
        cls.dotnet = (DOTNET / "EuroOfficeSupervisedPinnedV2Candidate.cs").read_text("utf-8")

    def test_native_capture_keeps_v1_default_and_strict_v2_stdout_bound(self):
        self.assertIn("max_stdout_bytes: int = join.MAX_NATIVE_BYTES + 1", self.v1)
        self.assertIn("if len(collected) > max_stdout_bytes:", self.v1)
        self.assertIn('"--open-staged-native-saveas-event-v2-lab"', self.controller)
        self.assertIn("max_stdout_bytes=event_v2.MAX_BYTES + 1", self.controller)
        self.assertIn("joined_v2.from_stream(", self.controller)
        self.assertIn("approved_disposable_vm", self.controller)
        self.assertIn("os.path.lexists(output)", self.controller)

    def test_native_runtime_pins_require_two_libraries_and_no_injected_environment(self):
        for token in (
            'ROLES = ("ascdocumentscore", "qtascdocumentscore")',
            "set(libs) != set(ROLES)",
            "LD_LIBRARY_PATH",
            "PATH",
            "sys.dont_write_bytecode = True",
            "_runtime_digest(library_paths[role]) != library_hashes[role]",
            "old._binary_digest(binary) != binary_before",
            'b"\\x7fELF"',
            "ENV_FIELDS",
        ):
            self.assertIn(token, self.controller)
        self.assertNotIn('"LD_PRELOAD",', self.controller)
        self.assertNotIn('"QT_PLUGIN_PATH",', self.controller)
        self.assertNotIn("shell=True", self.controller)
        self.assertNotIn("subprocess.Popen(", self.controller)  # Uses bounded v1 process primitive.
        self.assertNotIn("write_bytes(", self.controller)
        self.assertNotIn("os.replace(", self.controller)

    def test_nested_dotnet_diagnostic_never_grants_rights(self):
        for token in (
            'public bool NativeSaveProven => false;',
            'public bool ApprovedForProduction => false;',
            'getdoc-native-pinned-v2-supervised-ooxml-lab/1',
            'getdoc-native-pinned-v2-ooxml-joined-lab/1',
            "Exact(libraryPins, RuntimeKeys)",
            "Exact(candidate, NestedKeys)",
        ):
            self.assertIn(token, self.dotnet)
        self.assertNotIn("File.WriteAllBytes", self.dotnet)


if __name__ == "__main__":
    unittest.main()
