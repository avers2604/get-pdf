"""GD-31J.8: static contract for the Qt-only IPC mode.

Authored for the deferred final offline acceptance; no tests are executed now.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent
NATIVE = (ROOT / "native_host/ipc_probe.cpp").read_text(encoding="utf-8")
QT_MAIN = (ROOT / "native_host/main.cpp").read_text(encoding="utf-8")


class QtTabIpcContractTests(unittest.TestCase):
    def test_ipc_is_an_explicit_separate_no_editor_mode(self):
        self.assertIn('mode == "--ipc-probe"', QT_MAIN)
        self.assertIn('QCoreApplication application(argc, argv)', QT_MAIN)
        self.assertIn('RunIpcProbe()', QT_MAIN)
        self.assertIn('getdoc-eurodesktop-ipc/1', NATIVE)

    def test_file_document_and_print_commands_are_absent(self):
        for forbidden in ('OpenLocalFile(', 'CreateCefEditor(', 'CreateCefView(',
                          'save-as-new-revision', 'export-pdf', 'QFileDialog',
                          'QPrinter', 'QProcess::start', 'system(', 'popen('):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, NATIVE)
        self.assertIn('command == "attach-tab"', NATIVE)
        self.assertIn('command == "resize"', NATIVE)
        self.assertIn('command == "close"', NATIVE)

    def test_bound_messages_and_exact_identity_ordering(self):
        self.assertIn('constexpr int kMaxBytes = 4096;', NATIVE)
        self.assertIn('messages < 128', NATIVE)
        self.assertIn('lastSeq + 1, lastSeq + 1', NATIVE)
        self.assertIn('incomingTab != tabId', NATIVE)
        self.assertIn('toJson(QJsonDocument::Compact) == bytes', NATIVE)


if __name__ == "__main__":
    unittest.main()
