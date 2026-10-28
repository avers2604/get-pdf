"""GD-31J.8 native process IPC: only tab lifecycle, no file operations.

Requires the separately compiled Qt-only diagnostic binary; tests are authored
now and intentionally not executed until the final offline laboratory phase.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest

PROTOCOL = "getdoc-eurodesktop-ipc/1"
PINNED = "6a671bb1ad238c61965a64de3a005c420940c814"
SESSION = "25fe787823da4d76b28d2fd5ffb187e8"
TAB = "b70b9c8b73414d99ae3b2db31d9c25c7"


def request(command, **kwargs):
    obj = {"command": command, "protocol": PROTOCOL, "sessionId": SESSION}
    if command == "hello":
        obj["upstreamCommit"] = PINNED
    else:
        obj["sequence"] = kwargs.pop("sequence")
        obj["tabId"] = TAB
    obj.update(kwargs)
    return json.dumps(obj, sort_keys=True, separators=(",", ":"))


class QtNativeIpcTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        file = os.environ.get("GD31J8_QT_IPC_BINARY")
        if not file:
            raise unittest.SkipTest("Qt-only IPC binary not supplied")
        cls.binary = Path(file).resolve(strict=True)

    def invoke(self, commands):
        return subprocess.run(
            [str(self.binary), "--ipc-probe"],
            input=("\n".join(commands) + "\n").encode(),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=10, check=False,
        )

    def test_connect_attach_resize_and_close(self):
        result = self.invoke([
            request("hello"),
            request("attach-tab", sequence=1),
            request("resize", sequence=2, width=1200, height=800, scale=1.5),
            request("close", sequence=3)
        ])
        self.assertEqual(0, result.returncode)
        messages = [json.loads(line) for line in result.stdout.decode().splitlines()]
        self.assertEqual(["hello-ack", "ack", "ack", "ack"], [m["command"] for m in messages])
        self.assertEqual(["ready", "attached", "resized", "closed"], [m["state"] for m in messages])
        self.assertEqual([], messages[0]["features"])
        self.assertTrue(all(m["sessionId"] == SESSION for m in messages))
        self.assertEqual([0, 1, 2, 3], [m["sequence"] for m in messages])

    def test_session_bound_qt_only_png_frame_not_an_office_document(self):
        result = self.invoke([
            request("hello"),
            request("attach-tab", sequence=1),
            request("request-frame", sequence=2),
            request("close", sequence=3)
        ])
        self.assertEqual(0, result.returncode,
                         result.stderr.decode(errors="replace"))
        messages = [json.loads(line) for line in result.stdout.decode().splitlines()]
        self.assertEqual(4, len(messages))
        frame = messages[2]
        self.assertEqual("frame", frame["command"])
        self.assertEqual("preview", frame["state"])
        self.assertEqual("qt-painter-test-only", frame["source"])
        self.assertEqual((320, 180), (frame["width"], frame["height"]))
        self.assertEqual(2, frame["sequence"])
        self.assertEqual(SESSION, frame["sessionId"])
        self.assertEqual(TAB, frame["tabId"])
        png = base64.b64decode(frame["payloadBase64"], validate=True)
        self.assertLessEqual(len(png), 48 * 1024)
        self.assertEqual(b"\x89PNG\r\n\x1a\n", png[:8])
        self.assertEqual(hashlib.sha256(png).hexdigest(), frame["sha256"])

    def test_denies_document_operations(self):
        for command in ("open-local", "save-as-new-revision", "export-pdf",
                        "print-direct", "execute-command", "window-handle"):
            with self.subTest(command=command):
                result = self.invoke([request("hello"), request(command, sequence=1)])
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(1, len(result.stdout.splitlines()))  # only hello ACK

    def test_denies_replay_wrong_session_out_of_order(self):
        for commands, expected_replies in (
            ([request("hello"), request("attach-tab", sequence=2)], 1),
            ([request("hello"), request("attach-tab", sequence=1).replace(SESSION, "0" * 32)], 1),
            ([request("hello"), request("attach-tab", sequence=1) + " "], 1),
            ([request("hello"), request("attach-tab", sequence=1),
              request("resize", sequence=2, width=800, height=600, scale=1.0,
                      tabId="f" * 32)], 2),
            ([request("hello"), request("attach-tab", sequence=1),
              request("close", sequence=1)], 2),
        ):
            with self.subTest(commands=commands):
                result = self.invoke(commands)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(expected_replies, len(result.stdout.splitlines()))


if __name__ == "__main__":
    if len(sys.argv) == 2 and not sys.argv[1].startswith("-"):
        os.environ["GD31J8_QT_IPC_BINARY"] = sys.argv.pop(1)
    unittest.main()
