"""GD-31J.9 source contracts for diagnostic native tabs in GET's single UI.

No GUI test or Qt/CEF process is launched by this source check. The actual
Windows/Astra UI tests remain in the separate final offline acceptance plan.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
MAIN = ROOT / "src/GetDocument.Desktop/MainWindow.cs"
PANE = ROOT / "src/GetDocument.Desktop/Office/NativeOfficeDiagnosticPane.cs"


class NativeTabShellContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.main = MAIN.read_text(encoding="utf-8")
        cls.pane = PANE.read_text(encoding="utf-8")

    def test_uses_existing_tab_control_and_not_a_new_window(self):
        self.assertIn("private readonly TabControl _tabs = new();", self.main)
        self.assertIn("_items.Add(tab);", self.main)
        self.assertIn("_tabs.SelectedItem = tab;", self.main)
        self.assertIn("new NativeOfficeDiagnosticPane(snapshot)", self.main)
        self.assertNotIn("new Window()", self.pane)
        self.assertNotIn("new EmbeddedWindowHost()", self.pane)

    def test_native_and_existing_workspace_selection_are_separate(self):
        self.assertIn("if (_views.ContainsKey(id))", self.main)
        self.assertIn("else if (_nativeTabViews.ContainsKey(id))", self.main)
        self.assertIn("_nativeWorkspaceTabs.Select(id);", self.main)
        self.assertIn("_workspace.Select(id);", self.main)

    def test_unsaved_changes_and_printing_are_not_silently_dropped(self):
        self.assertIn("snapshot.ModifiedObserved", self.main)
        self.assertIn("CloseNativeDiagnosticTabAsync", self.main)
        self.assertIn("GetDialogs.ConfirmAsync", self.main)
        self.assertIn("Печать из диагностического EuroOffice запрещена", self.main)
        self.assertIn("_nativeWorkspaceTabs.HostCrashed(", self.main)

    def test_no_native_editor_autostart_or_claim_of_success(self):
        for forbidden in ("new QCefView(", "OpenLocalFile(", "CreateCefEditor(",
                          "EuroOfficeNativeIpcProcessSession.StartAsync("):
            self.assertNotIn(forbidden, self.pane)
        self.assertIn("не подтвержден", self.pane.lower())
        self.assertIn("не подтвержден", self.main.lower())


if __name__ == "__main__":
    unittest.main()
