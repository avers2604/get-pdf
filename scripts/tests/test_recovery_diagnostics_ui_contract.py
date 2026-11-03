"""GD-31J.27ap offline source-only safety contract (NOT an Avalonia GUI test)."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"
WINDOW = ROOT / "src/GetDocument.Desktop/MainWindow.cs"
THEME = ROOT / "src/GetDocument.Desktop/Theme/GetTheme.axaml"


class RecoveryDiagnosticsSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.window = WINDOW.read_text(encoding="utf-8")
        cls.theme = THEME.read_text(encoding="utf-8")

    def test_diagnostics_is_purely_observational(self):
        for forbidden in (
            "File.Delete(", "File.Move(", "File.Copy(", "File.Write",
            "File.Read", "File.Open(", "File.Create(", "Directory.Delete(",
            "Directory.CreateDirectory(", "Process.Start(", "ExportRecoveryCopy",
            "CaptureAsync(", "Discard(", "Prune(", "Restore(", "SaveAs(",
            "ShellExecute", "ProcessStartInfo", "HttpClient(", "Socket(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.pane)
        self.assertIn("InspectIncomplete(", self.pane)
        self.assertIn("Task.Run(", self.pane)
        self.assertIn("CancellationToken token = inspection.Token;", self.pane)
        self.assertIn("CancellationTokenSource.CreateLinkedTokenSource(", self.pane)
        self.assertIn("_lifetime.Cancel();", self.pane)
        self.assertIn("_refresh.IsEnabled = false;", self.pane)

    def test_only_one_user_action_and_no_destructive_buttons(self):
        self.assertEqual(1, self.pane.count("readonly Button "))
        self.assertIn('Content = "Обновить сведения"', self.pane)
        self.assertIn("_refresh.Click +=", self.pane)
        self.assertNotIn('Content = "Удалить"', self.pane)
        self.assertNotIn('Content = "Восстановить"', self.pane)
        self.assertIn("Автоматическое удаление и восстановление отключены", self.pane)
        self.assertIn("Только просмотр временных файлов", self.pane)

    def test_info_states_are_explicit_and_never_certify_recovery(self):
        for symbol in (
            "OfficeRecoveryIncompleteStatus.RegularUnverified",
            "OfficeRecoveryIncompleteStatus.UntrustedLinkOrNonFile",
            "OfficeRecoveryIncompleteStatus.MetadataUnavailable",
            "result.Truncated",
            "Список неполон",
            "не подтверждают",
            "не проверен",
            "не обнаружены",
            "Доступ или безопасность каталога не подтверждены",
        ):
            with self.subTest(symbol=symbol):
                self.assertIn(symbol, self.pane)

    def test_existing_ds_41_styles_are_reused_not_recreated(self):
        for name in ('"title"', '"hint"', '"section"', '"card"', '"toolbar"'):
            self.assertIn(name, self.pane)
        for color in ("GetNavyBrush", "GetMutedBrush", "GetRedBrush"):
            self.assertIn(color, self.pane)
            self.assertIn(color, self.theme)
        self.assertIn("GetDocument.Desktop.Recovery", self.window)

    def test_single_tab_home_entry_and_source_scoping(self):
        for marker in (
            'CreateAction("Аварийные копии"',
            "ShowRecoveryDiagnostics",
            "_recoveryTabs.TryGetValue(source.Id, out var existing)",
            "source?.FilePath is not { } path",
            'new[] { ".docx", ".xlsx", ".pptx" }',
            "new RecoveryDiagnosticsPane(path)",
            "_items.Add(view);",
            "_tabs.SelectedItem = view;",
            "CloseRecoveryDiagnostics(sourceId)",
            "CloseRecoveryDiagnostics(id, restoreSourceSelection: false);",
            "foreach (var inspection in _recoveryTabs.Values)",
            "_recoveryTabs.FirstOrDefault(",
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, self.window)
        self.assertIn("CloseRecoveryDiagnostics(recovery.Key)", self.window)
        self.assertIn("inspection.ResumeInspection();", self.window)


if __name__ == "__main__":
    unittest.main()
