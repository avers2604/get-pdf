"""Tests for the GD-31J non-editing transport double; no Qt or documents."""
import json
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
import mock_host


class NativeProbeMockTests(unittest.TestCase):
    def test_hello_advertises_nothing_and_is_pinned(self):
        request = {
            "command": "hello",
            "protocol": mock_host.PROTOCOL,
            "upstreamCommit": mock_host.PINNED,
        }
        completed = subprocess.run(
            [sys.executable, str(ROOT / "mock_host.py")],
            input=(json.dumps(request) + "\n").encode("utf-8"),
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10, check=False)
        self.assertEqual(0, completed.returncode)
        hello = json.loads(completed.stdout)
        self.assertEqual([], hello["features"])
        self.assertEqual(mock_host.PINNED, hello["upstreamCommit"])
        self.assertEqual("EuroOfficeDesktopEditors", hello["engine"])

    def test_file_and_print_operations_are_rejected(self):
        for cmd in ("open-local", "save-as-new-revision", "export-pdf", "print-direct", "run"):
            completed = subprocess.run(
                [sys.executable, str(ROOT / "mock_host.py")],
                input=(json.dumps({
                    "command": cmd,
                    "protocol": mock_host.PROTOCOL,
                    "upstreamCommit": mock_host.PINNED,
                    "source": "/sensitive/document.docx"
                }) + "\n").encode(),
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10, check=False)
            self.assertEqual(2, completed.returncode)
            self.assertEqual(b"", completed.stdout)


if __name__ == "__main__":
    unittest.main()
