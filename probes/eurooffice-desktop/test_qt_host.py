"""Run a REAL compiled GD-31J.2 Qt process in headless mode.

Requires a local Qt 6 build. Does not open any document, network or desktop window.
The process must expose ZERO editing capabilities and reject every non-hello input.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest


PINNED = "6a671bb1ad238c61965a64de3a005c420940c814"
PROTOCOL = "getdoc-eurodesktop-probe/1"
GOOD = {"command": "hello", "protocol": PROTOCOL, "upstreamCommit": PINNED}


class QtHostBinaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if len(sys.argv) > 1 and not sys.argv[1].startswith("-"):
            cls.exe = Path(sys.argv.pop(1)).resolve()
        else:
            hint = os.environ.get("GD31J2_QT_HOST_BINARY")
            if not hint:
                raise unittest.SkipTest("Local Qt 6 native host binary not supplied")
            cls.exe = Path(hint).resolve()
        if not cls.exe.is_file():
            raise RuntimeError("GD-31J.2 compiled Qt host missing: " + str(cls.exe))

    def invoke(self, request):
        return subprocess.run(
            [str(self.exe), "--headless-probe"],
            input=request,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=8,
            check=False,
        )

    def test_exact_handshake_returns_no_editing_permissions(self):
        reply = self.invoke(json.dumps(GOOD, separators=(",", ":")).encode() + b"\n")
        self.assertEqual(0, reply.returncode)
        self.assertEqual(b"", reply.stderr)
        response = json.loads(reply.stdout)
        self.assertEqual("EuroOfficeDesktopEditors", response["engine"])
        self.assertEqual(PINNED, response["upstreamCommit"])
        self.assertEqual([], response["features"])
        self.assertEqual(4, len(response))

    def test_fail_closed_for_documents_print_and_unknown_commands(self):
        for command in ("open-local", "save-as-new-revision", "export-pdf",
                        "print-direct", "run-macro", "close"):
            bad = dict(GOOD, command=command)
            with self.subTest(command=command):
                reply = self.invoke(json.dumps(bad, separators=(",", ":")).encode() + b"\n")
                self.assertNotEqual(0, reply.returncode)
                self.assertEqual(b"", reply.stdout)

    def test_rejects_duplicate_keys_wrong_revision_and_unbounded_input(self):
        cases = [
            b'{"command":"hello","command":"hello","protocol":"getdoc-eurodesktop-probe/1","upstreamCommit":"' +
                PINNED.encode() + b'"}\n',
            json.dumps(dict(GOOD, upstreamCommit="a" * 40), separators=(",", ":")).encode() + b"\n",
            json.dumps(GOOD, indent=2).encode() + b"\n",
            b"X" * 17000 + b"\n",
            b"",
        ]
        for index, case in enumerate(cases):
            with self.subTest(index=index):
                reply = self.invoke(case)
                self.assertNotEqual(0, reply.returncode)
                self.assertEqual(b"", reply.stdout)


if __name__ == "__main__":
    unittest.main()
