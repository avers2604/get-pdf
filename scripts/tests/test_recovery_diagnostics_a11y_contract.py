"""GD-31J.27aq source-only a11y/keyboard acceptance, NOT rendered GUI proof."""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
PANE = ROOT / "src/GetDocument.Desktop/Recovery/RecoveryDiagnosticsPane.cs"
THEME = ROOT / "src/GetDocument.Desktop/Theme/GetTheme.axaml"
WINDOW = ROOT / "src/GetDocument.Desktop/MainWindow.cs"


def contrast(foreground: str, background: str) -> float:
    def luminance(rgb: str) -> float:
        channels = [int(rgb[i:i+2], 16) / 255 for i in (1, 3, 5)]
        linear = [v / 12.92 if v <= 0.04045 else ((v + .055) / 1.055)**2.4
                  for v in channels]
        return .2126*linear[0] + .7152*linear[1] + .0722*linear[2]
    a, b = sorted((luminance(foreground), luminance(background)))
    return (b + .05) / (a + .05)


class RecoveryA11ySourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pane = PANE.read_text(encoding="utf-8")
        cls.theme = THEME.read_text(encoding="utf-8")
        cls.window = WINDOW.read_text(encoding="utf-8")

    def test_f5_refresh_keyboard_routed_with_no_new_destructive_actions(self):
        self.assertIn("using Avalonia.Input;", self.pane)
        self.assertIn("KeyDown += OnPaneKeyDown;", self.pane)
        self.assertIn("e.Key != Key.F5", self.pane)
        self.assertIn("e.Handled = true;", self.pane)
        self.assertIn("_ = RefreshAsync();", self.pane)
        self.assertIn("if (_disposed || _busy) return;", self.pane)
        self.assertEqual(1, self.pane.count("readonly Button "))
        for forbidden in ("File.Delete(", "File.Write", "File.Move(", "Discard(",
                          "CaptureAsync(", "Process.Start(", "Restore("):
            self.assertNotIn(forbidden, self.pane)

    def test_focus_is_available_when_new_diagnostics_tab_attaches(self):
        self.assertIn("Focusable = true;", self.pane)
        self.assertIn("AttachedToVisualTree +=", self.pane)
        self.assertIn("Focus();", self.pane)
        self.assertIn("CloseRecoveryDiagnostics(id);", self.window)
        self.assertIn("_lifetime.Cancel();", self.pane)

    def test_dynamic_live_region_name_tracks_real_summary_not_constant(self):
        self.assertIn("AutomationLiveSetting.Polite", self.pane)
        self.assertIn("private void UpdateAccessibleStatus()", self.pane)
        self.assertIn('"Состояние диагностики: " + _summary.Text', self.pane)
        self.assertIn("UpdateAccessibleStatus();", self.pane)
        self.assertGreaterEqual(self.pane.count("UpdateAccessibleStatus();"), 4)
        self.assertNotIn('SetName(_summary, "Состояние диагностики");', self.pane)

    def test_warning_text_handles_truncation_and_untrusted_independently(self):
        self.assertIn("if (result.Truncated)", self.pane)
        self.assertIn("if (untrusted > 0)", self.pane)
        self.assertIn("Список неполон", self.pane)
        self.assertIn("Недоверенных или недоступных объектов", self.pane)
        self.assertIn("string.Join(", self.pane)
        self.assertIn("не подтверждает", self.pane)

    def test_actions_wrap_on_smaller_width_and_keep_inherent_tab_order(self):
        self.assertIn("var controls = new WrapPanel", self.pane)
        self.assertIn("controls.Children.Add(_refresh);", self.pane)
        self.assertIn("controls.Children.Add(scope);", self.pane)
        self.assertIn("TextWrapping = TextWrapping.Wrap", self.pane)
        self.assertNotIn("Orientation = Orientation.Horizontal,\n            Spacing = 14,",
                         self.pane)

    def test_brandbook_text_colors_meet_normal_45_to_1_white_background(self):
        palette = {}
        for key, value in re.findall(
                r'<Color x:Key="(Get(?:Red|Muted|Navy|Surface))">(#(?:[A-Fa-f0-9]{6}))</Color>',
                self.theme):
            palette[key] = value
        self.assertEqual(4, len(palette))
        for key in ("GetRed", "GetMuted", "GetNavy"):
            with self.subTest(key=key):
                self.assertGreaterEqual(contrast(palette[key], palette["GetSurface"]), 4.5)


if __name__ == "__main__":
    unittest.main()
