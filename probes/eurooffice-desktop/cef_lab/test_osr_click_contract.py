"""GD-31J.15 deferred source contracts for opt-in synthetic CEF click/drag.

Static assertions, NOT a claim of successful Qt/CEF/.NET execution.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
CEF = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"
DESKTOP = ROOT / "src/GetDocument.Desktop"

class OsrClickSafetyContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cmake = (CEF / "CMakeLists.txt").read_text("utf-8")
        cls.cpp = (CEF / "osr_input.cpp").read_text("utf-8")
        cls.entry = (CEF / "cef_staged_open.cpp").read_text("utf-8")
        cls.channel = (OFFICE / "EuroOfficeOsrLabInputChannel.cs").read_text("utf-8")
        cls.process = (OFFICE / "EuroOfficeOsrStreamProcessLab.cs").read_text("utf-8")
        cls.pane = (DESKTOP / "Office/NativeOfficeDiagnosticPane.cs").read_text("utf-8")
        cls.main = (DESKTOP / "MainWindow.cs").read_text("utf-8")

    def test_new_click_lab_requires_independent_default_off_gate(self):
        self.assertIn('GETDOC_GD31J15_ENABLE_CLICK_LAB', self.cmake)
        self.assertIn('selection drag experiment" OFF)', self.cmake)
        self.assertIn('GETDOC_GD31J14_ENABLE_INPUT_LAB', self.cmake)
        self.assertIn('GETDOC_GD31J15_CLICK_LAB=1', self.cmake)
        self.assertIn('#ifndef GETDOC_GD31J15_CLICK_LAB', self.entry)
        self.assertIn('--open-staged-wayland-click-lab', self.entry)
        self.assertIn('OsrInputPump inputPump(streamSession, streamTab, clickLab,', self.entry)
        self.assertIn('keyboardLab, imeCommitLab);', self.entry)

    def test_native_clicks_are_paired_and_double_click_count_bounded(self):
        for needle in ('m_clicksApproved', 'm_leftDown', 'm_clickCount',
                       'command == "left-down"', 'command == "left-up"',
                       'BoundedInteger(request.value("clickCount"), 1, 2)',
                       'SendMouseClickEvent(x, y, 1, false',
                       'SendMouseClickEvent(x, y, 1, true',
                       'm_leftDown ? kCefLeftButton : 0',
                       'constexpr int kCefLeftButton = 1 << 4',
                       'if (!enable && m_leftDown) return false;'):
            self.assertIn(needle, self.cpp)
        for forbidden in ('QClipboard', 'QInputMethodEvent',
                          'ExecuteJavaScript(', 'QFileDialog', 'QPrinter'):
            self.assertNotIn(forbidden, self.cpp)
        # Later opt-in keyboard labs legitimately add SendKeyEvent to this file.
        # Only the click handler itself must remain free of keyboard events.
        click_only = self.cpp.split('command == "left-down"', 1)[1].split(
            'command == "nav-key"', 1)[0]
        self.assertNotIn('SendKeyEvent(', click_only)

    def test_channel_rejects_keyboard_clipboard_without_explicit_approval(self):
        for needle in ('clickLabApproved = false', 'SendLeftButtonDownAsync(',
                       'SendLeftButtonUpAsync(', 'clickCount is < 1 or > 2',
                       'if (!_clickLabApproved)', 'if (_leftDown)',
                       'if (!_leftDown)', 'RequireFocus()', 'SendKeyboardOrIme()',
                       'ShareClipboard()', 'SendMouseClick()'):
            self.assertIn(needle, self.channel)
        self.assertIn('CreateClickStartInfo(', self.process)
        self.assertIn('clickLabApproved: clickLabApproved', self.process)

    def test_avalonia_capture_prevents_stuck_button_and_selection_stays_lab_only(self):
        for needle in ('PointerReleased += OnLabPointerReleased',
                       'PointerCaptureLost += OnLabPointerCaptureLost',
                       'SendLeftButtonDownAsync(', 'SendLeftButtonUpAsync(',
                       'ReleaseLabButtonAsync(', 'SuspendLabInputAsync()',
                       '_labGestureGate', 'clickCount', 'TryNormalizedPosition('):
            self.assertIn(needle, self.pane)
        self.assertIn('pointerClickLabApproved = false', self.main)
        self.assertIn('if (pointerClickLabApproved && !pointerInputLabApproved)', self.main)
        self.assertIn('CreateClickStartInfo(', self.main)
        self.assertIn('pointerClickLabApproved)', self.main)

if __name__ == "__main__":
    unittest.main()
