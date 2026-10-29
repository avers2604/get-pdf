"""GD-31J.16 deferred static safety contract; no execution of Qt/CEF/.NET.

This checks structural restrictions, NOT functional correctness or isolation.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
CEF=ROOT/"probes/eurooffice-desktop/cef_lab"
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
DESKTOP=ROOT/"src/GetDocument.Desktop"

class CefKeyboardLabContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cmake=(CEF/"CMakeLists.txt").read_text("utf-8")
        cls.entry=(CEF/"cef_staged_open.cpp").read_text("utf-8")
        cls.native=(CEF/"osr_input.cpp").read_text("utf-8")
        cls.channel=(OFFICE/"EuroOfficeOsrLabInputChannel.cs").read_text("utf-8")
        cls.process=(OFFICE/"EuroOfficeOsrStreamProcessLab.cs").read_text("utf-8")
        cls.main=(DESKTOP/"MainWindow.cs").read_text("utf-8")
        cls.pane=(DESKTOP/"Office/NativeOfficeDiagnosticPane.cs").read_text("utf-8")

    def test_keyboard_lab_is_separately_off_and_keeps_old_modes(self):
        self.assertIn('GETDOC_GD31J16_ENABLE_KEYBOARD_LAB', self.cmake)
        self.assertIn('character experiment" OFF)', self.cmake)
        self.assertIn('GETDOC_GD31J15_ENABLE_CLICK_LAB', self.cmake)
        self.assertIn('GETDOC_GD31J16_KEYBOARD_LAB=1', self.cmake)
        self.assertIn('#ifndef GETDOC_GD31J16_KEYBOARD_LAB', self.entry)
        self.assertIn('--open-staged-wayland-keyboard-lab', self.entry)
        self.assertIn('inputPump(streamSession, streamTab, clickLab,', self.entry)
        self.assertIn('keyboardLab, imeCommitLab);', self.entry)

    def test_native_cannot_accept_arbitrary_text_or_system_shortcuts(self):
        for needle in ('ApprovedNavigationVk(', '"ArrowLeft"', '"PageDown"',
                       '"Backspace"', '"Enter"', '"Tab"', 'AllowedCommittedCharacter',
                       'm_keyboardApproved', 'command == "nav-key"',
                       'command == "text-char"', 'm_focused', 'm_leftDown',
                       'SendKeyEvent(0, vk, modifiers, L"")',
                       'SendKeyEvent(2, vk, modifiers, L"")',
                       'SendKeyEvent(0, 0, 0, units)',
                       'SendKeyEvent(3, 0, 0, units)',
                       'SendKeyEvent(2, 0, 0, units)'):
            self.assertIn(needle, self.native)
        for forbidden in ('QClipboard', 'QInputMethodEvent', 'QFileDialog',
                          'ExecuteJavaScript(', 'ImeCommitText(', 'QPrinter'):
            self.assertNotIn(forbidden, self.native)

    def test_managed_channel_has_whitelist_without_clipboard_or_ime_preedit(self):
        for needle in ('keyboardLabApproved = false', 'AllowedNavigationKeys',
                       'SendNavigationKeyAsync(', 'SendTextCharacterAsync(',
                       'SendImeComposition()', 'ShareClipboard()',
                       'SendKeyboardOrIme()', 'RequireKeyboardReady()'):
            self.assertIn(needle, self.channel)
        self.assertIn('CreateKeyboardStartInfo(', self.process)
        self.assertIn('keyboardLabApproved: keyboardLabApproved', self.process)

    def test_ui_owns_shell_shortcuts_and_never_forwards_string_payloads(self):
        for needle in ('_preview.KeyDown += OnLabKeyDown',
                       '_preview.KeyUp += OnLabKeyUp',
                       '_preview.TextInput += OnLabTextInput',
                       'KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta',
                       'e.Text is not { Length: 1 }',
                       'SendNavigationKeyAsync(', 'SendTextCharacterAsync(',
                       '_labReservedModifierActive', 'KeyboardLabApproved'):
            self.assertIn(needle, self.pane)
        self.assertIn('keyboardLabApproved = false', self.main)
        self.assertIn('if (keyboardLabApproved && !pointerClickLabApproved)', self.main)
        self.assertIn('CreateKeyboardStartInfo(', self.main)

if __name__=="__main__":
    unittest.main()
