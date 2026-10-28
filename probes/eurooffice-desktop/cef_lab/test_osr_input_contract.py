"""GD-31J.14 native input source safety contract; deferred, not executed."""
from pathlib import Path
import unittest

HERE=Path(__file__).resolve().parent
NATIVE=(HERE/"cef_staged_open.cpp")
CMAKE=(HERE/"CMakeLists.txt")

class OsrPointerInputContract(unittest.TestCase):
    def test_only_wayland_and_separate_default_off_flag(self):
        c=CMAKE.read_text("utf-8")
        s=NATIVE.read_text("utf-8")
        self.assertIn("GETDOC_GD31J14_ENABLE_INPUT_LAB",c)
        self.assertIn('pointer input experiment" OFF)',c)
        self.assertIn("GETDOC_GD31J14_POINTER_INPUT=1",c)
        self.assertIn("GETDOC_GD31J14_POINTER_INPUT",s)
        self.assertIn('QT_QPA_PLATFORM',s)

    def test_input_sequence_and_bounded_nonblocking_stdin(self):
        s=NATIVE.read_text("utf-8")
        for text in ("O_NONBLOCK", "STDIN_FILENO", "EAGAIN", "lastSequence",
                     "frameNumber", "getdoc-cef-osr-input/1",
                     "sessionId", "tabId", "x10000", "y10000",
                     "QJsonDocument(request).toJson(QJsonDocument::Compact)",
                     "SendMouseMoveEvent", "SendMouseWheelEvent",
                     "focus(", "devicePixelRatio"):
            self.assertIn(text,s)

    def test_no_key_click_ime_or_clipboard_dispatch(self):
        s=NATIVE.read_text("utf-8")
        self.assertNotIn("editor->SendKeyEvent(",s)
        self.assertNotIn("editor->SendMouseClickEvent(",s)
        self.assertNotIn("QClipboard",s)
        self.assertNotIn("QInputMethodEvent",s)

if __name__=="__main__":
    unittest.main()
