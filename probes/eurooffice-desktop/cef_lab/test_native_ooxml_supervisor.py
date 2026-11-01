"""GD-31J.27a: offline-only supervised native stdout correlation tests.

All process captures are injected fakes: no Qt, CEF or corporate documents.
The local acceptance runner will execute these tests later.
"""
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_ooxml_supervisor as supervisor
import native_ooxml_join as join
import synthetic_roundtrip as ooxml
from test_native_ooxml_join import SESSION, TAB, native_receipt, setup_case


class SupervisedNativeReceiptTests(unittest.TestCase):
    def prepare(self, tmp):
        root = Path(tmp)
        # The fixture helper expects an already existing root directory.
        source = root / "staging"
        source.mkdir()
        staging, output, _, _ = setup_case(source, ooxml.FIXTURES[0])
        content = output.read_bytes()
        digest = hashlib.sha256(content).hexdigest()
        output.unlink()  # Must not exist before native SaveAs.
        binary = root / "native-host"
        binary.write_bytes(b"#!/bin/sh\nexit 0\n")
        binary.chmod(0o700)
        pin = hashlib.sha256(binary.read_bytes()).hexdigest()
        return staging, output, content, digest, binary, pin

    def exercise(self, staging, output, content, digest, binary, pin, *,
                 receipt_changes=None, exit_code=0, extra=b"", mutate_host=False,
                 ack=True, platform="wayland"):
        name = ooxml.FIXTURES[0]
        def fake_capture(argv, env):
            self.assertEqual(str(binary), argv[0])
            self.assertEqual(["--open-staged-native-saveas-lab", str(staging),
                              name, SESSION, TAB], argv[1:])
            self.assertEqual("wayland", env["QT_QPA_PLATFORM"])
            output.write_bytes(content)
            if mutate_host:
                binary.write_bytes(b"tampered\n")
            native = native_receipt(name, digest, **(receipt_changes or {}))
            return exit_code, (native + "\n").encode("ascii") + extra
        return supervisor.run_supervised(
            staging, name, SESSION, TAB, binary, pin,
            approved_disposable_vm=ack,
            env={"QT_QPA_PLATFORM": platform}, capture=fake_capture)

    def test_accepted_synthetic_candidate_retains_denied_privileges(self):
        with tempfile.TemporaryDirectory() as tmp:
            args = self.prepare(tmp)
            receipt = self.exercise(*args)
            self.assertEqual(supervisor.ENVELOPE_SCHEMA, receipt["schema"])
            self.assertTrue(receipt["nativeProcessExitZero"])
            self.assertTrue(receipt["nativeBinaryPinMatched"])
            self.assertTrue(receipt["stdoutSingleLineBounded"])
            self.assertEqual(args[-1], receipt["nativeBinarySha256"])
            self.assertTrue(receipt["joinedCandidate"]["outputDigestCorrelated"])
            self.assertFalse(receipt["nativeSaveProven"])
            self.assertFalse(receipt["approvedForProduction"])
            self.assertFalse(receipt["joinedCandidate"]["documentVerified"])
            self.assertEqual("getdoc-native-ooxml-joined-candidate-lab/1",
                             receipt["joinedCandidate"]["schema"])

    def test_refuses_unapproved_vm_wrong_platform_and_wrong_binary_pin(self):
        for change in ("ack", "platform", "pin"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as tmp:
                staging, output, content, digest, binary, pin = self.prepare(tmp)
                kwargs = {"approved_disposable_vm": True,
                          "env": {"QT_QPA_PLATFORM": "wayland"},
                          "capture": lambda argv, env: (0, b"")}
                if change == "ack":
                    kwargs["approved_disposable_vm"] = False
                elif change == "platform":
                    kwargs["env"] = {"QT_QPA_PLATFORM": "offscreen"}
                else:
                    pin = "0" * 64
                with self.assertRaises(supervisor.SupervisorError):
                    supervisor.run_supervised(staging, ooxml.FIXTURES[0], SESSION, TAB,
                                              binary, pin, **kwargs)
                self.assertFalse(output.exists())

    def test_refuses_existing_output_before_process_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            staging, output, content, _, binary, pin = self.prepare(tmp)
            output.write_bytes(content)
            called = []
            with self.assertRaises(supervisor.SupervisorError):
                supervisor.run_supervised(staging, ooxml.FIXTURES[0], SESSION, TAB,
                                          binary, pin, approved_disposable_vm=True,
                                          env={"QT_QPA_PLATFORM": "wayland"},
                                          capture=lambda argv, env: called.append(True))
            self.assertFalse(called)

    def test_rejects_nonzero_exit_duplicate_stdout_and_mutating_host(self):
        for failure in ("exit", "extra", "mutate"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
                args = self.prepare(tmp)
                with self.assertRaises((supervisor.SupervisorError, join.JoinError)):
                    self.exercise(
                        *args, exit_code=7 if failure == "exit" else 0,
                        extra=b"garbage" if failure == "extra" else b"",
                        mutate_host=failure == "mutate")

    def test_false_callbacks_and_changed_digest_never_get_enveloped(self):
        for change in ({"nativeOnSaveAfterAnswer": False},
                       {"saveCancelledAfterAnswer": True},
                       {"outputSha256": "e"*64},
                       {"approvedForProduction": True}):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as tmp:
                args = self.prepare(tmp)
                with self.assertRaises((supervisor.SupervisorError, join.JoinError)):
                    self.exercise(*args, receipt_changes=change)


if __name__ == "__main__":
    unittest.main()
