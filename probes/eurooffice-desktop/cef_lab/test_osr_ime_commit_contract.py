"""GD-31J.17 deferred source gate: no runtime build, no OS clipboard access.

This is static architecture inspection, NOT proof of working OS IME/CEF.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
CEF = ROOT / "probes/eurooffice-desktop/cef_lab"
OFFICE = ROOT / "src/GetDocument.Office/EuroOfficeDesktop"
DESKTOP = ROOT / "src/GetDocument.Desktop"


class ImeFinalCommitContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cmake = (CEF / "CMakeLists.txt").read_text("utf-8")
        cls.native = (CEF / "osr_input.cpp").read_text("utf-8")
        cls.host = (CEF / "cef_staged_open.cpp").read_text("utf-8")
        cls.channel = (OFFICE / "EuroOfficeOsrLabInputChannel.cs").read_text("utf-8")
        cls.composition = (OFFICE / "EuroOfficeImeLabComposition.cs").read_text("utf-8")
        cls.process = (OFFICE / "EuroOfficeOsrStreamProcessLab.cs").read_text("utf-8")
        cls.pane = (DESKTOP / "Office/NativeOfficeDiagnosticPane.cs").read_text("utf-8")
        cls.main = (DESKTOP / "MainWindow.cs").read_text("utf-8")

    def test_ime_is_separately_default_off_and_still_wayland_only(self):
        self.assertIn('GETDOC_GD31J17_ENABLE_IME_COMMIT_LAB', self.cmake)
        self.assertIn('experiment without clipboard" OFF)', self.cmake)
        self.assertIn('GETDOC_GD31J16_ENABLE_KEYBOARD_LAB', self.cmake)
        self.assertIn('GETDOC_GD31J17_IME_COMMIT_LAB=1', self.cmake)
        self.assertIn('--open-staged-wayland-ime-commit-lab', self.host)
        self.assertIn('#ifndef GETDOC_GD31J17_IME_COMMIT_LAB', self.host)
        self.assertIn('inputPump(streamSession, streamTab, clickLab,', self.host)

    def test_native_validates_every_character_before_distributing_key_events(self):
        for must in (
            'command == "ime-commit"', 'm_imeCommitApproved',
            'ExactKeys(request, {"codePoints", "command"', 'units.size() > 16',
            'm_committedUnits + units.size() > 64',
            'AllowedCommittedCharacter(item.toInt())', 'QChar::Other_Format',
            'SendKeyEvent(0, 0, 0, character)',
            'SendKeyEvent(3, 0, 0, character)',
            'SendKeyEvent(2, 0, 0, character)'
        ):
            self.assertIn(must, self.native)
        for forbidden in (
            'QClipboard', 'ImeSetComposition(', 'ImeCommitText(',
            'QInputMethodEvent', 'QFileDialog', 'QPrinter'
        ):
            self.assertNotIn(forbidden, self.native)

    def test_preedit_is_local_and_cleared_on_cancel_or_tab_loss(self):
        for must in ('EuroOfficeImeLabComposition : IDisposable',
                     'MaxCommittedBmpUnits = 16', 'void Begin()', 'void Update(',
                     'void Cancel()', 'Array.Clear(_preedit)',
                     'channel.SessionId != _sessionId', 'SendImeCommittedTextAsync('):
            self.assertIn(must, self.composition)
        for must in ('_imeComposition?.Dispose()', '_imeComposition?.Cancel()',
                     'internal void BeginImeLabComposition()',
                     'internal async Task CommitImeLabCompositionAsync(',
                     'e.Text is not { Length: 1 }'):
            self.assertIn(must, self.pane)

    def test_no_os_clipboard_read_write_or_undelimited_string_paste(self):
        for must in ('RequireOsReadApproval()', 'RequireOsWriteApproval()',
                     'RequireHtmlOrRichTextApproval()', 'throw new NotSupportedException'):
            self.assertIn(must, self.composition)
        self.assertIn('imeCommitLabApproved = false', self.channel)
        self.assertIn('int[] codes = committed.Select', self.channel)
        self.assertIn('["codePoints"] = codes', self.channel)
        self.assertIn('CreateImeCommitStartInfo(', self.process)
        self.assertIn('imeCommitLabApproved: imeCommitLabApproved', self.process)
        self.assertIn('if (imeCommitLabApproved && !keyboardLabApproved)', self.main)


if __name__ == "__main__":
    unittest.main()
