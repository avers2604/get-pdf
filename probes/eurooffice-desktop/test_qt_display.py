"""GD-31J.5: run the real compiled, Qt-only display and lifecycle probe.

The tests deliberately use the offscreen Qt backend; a PASS is **not** evidence
for physical dual monitors, real DPI switching, CEF or Avalonia embedding.
A compiled local host is mandatory when these tests are invoked by CTest.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest

PROTOCOL = "getdoc-qt-display-diagnostics/1"


class DisplayProbeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        binary = os.environ.get("GD31J2_QT_HOST_BINARY")
        if not binary:
            raise RuntimeError("Explicit path to compiled Qt probe required")
        cls.executable = Path(binary).resolve(strict=True)
        if not cls.executable.is_file():
            raise RuntimeError("Qt probe binary missing")

    @classmethod
    def offscreen_env(cls):
        env = os.environ.copy()
        env["QT_QPA_PLATFORM"] = "offscreen"
        return env

    def run_probe(self, *args):
        return subprocess.run(
            [str(self.executable), *args],
            input=b"",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=12,
            env=self.offscreen_env(),
            check=False,
        )

    def test_offscreen_diagnostics_are_explicitly_not_real_multimonitor_proof(self):
        run = self.run_probe("--display-probe")
        self.assertEqual(0, run.returncode, run.stderr.decode("utf-8", errors="replace"))
        report = json.loads(run.stdout.decode("utf-8"))
        self.assertEqual(PROTOCOL, report["protocol"])
        self.assertEqual("qt-only-no-cef", report["runtimeKind"])
        self.assertTrue(report["closed"])
        self.assertFalse(report["cefInitialized"])
        self.assertFalse(report["realMonitorVerified"])
        self.assertGreaterEqual(report["screenCount"], 1)
        self.assertEqual(report["screenCount"], len(report["screens"]))
        self.assertGreaterEqual(report["windowEvents"]["show"], 1)
        self.assertGreaterEqual(report["windowEvents"]["resize"], 1)
        for screen in report["screens"]:
            self.assertGreater(screen["width"], 0)
            self.assertGreater(screen["height"], 0)
            self.assertGreater(screen["devicePixelRatio"], 0)
            self.assertGreater(screen["logicalDpi"], 0)

    def test_unknown_mode_and_document_arguments_remain_refused(self):
        for bad in (("--display-probe", "/tmp/file.docx"),
                    ("--display-probe", "--print"),
                    ("--display-probe", "--cef"),
                    ("--load-document",)):
            with self.subTest(args=bad):
                run = self.run_probe(*bad)
                self.assertNotEqual(0, run.returncode)
                self.assertEqual(b"", run.stdout)

    def test_process_can_be_terminated_and_reaped(self):
        process = subprocess.Popen(
            [str(self.executable), "--gui-probe"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            env=self.offscreen_env(),
        )
        try:
            # This mode blocks until a strict hello; a forced crash/shutdown
            # must still reap its OS process without leaving the test hanging.
            process.terminate()
            process.wait(timeout=5)
            self.assertIsNotNone(process.returncode)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            if process.stdin:
                process.stdin.close()
            if process.stdout:
                process.stdout.close()


if __name__ == "__main__":
    if len(sys.argv) == 2 and not sys.argv[1].startswith("-"):
        os.environ["GD31J2_QT_HOST_BINARY"] = sys.argv.pop(1)
    unittest.main()
