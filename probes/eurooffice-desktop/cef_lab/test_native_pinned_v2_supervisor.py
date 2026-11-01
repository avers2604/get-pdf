"""GD-31J.27d: offline-only synthetic pinned v2 supervisor tests (deferred).
All native capture is an injected fake: no actual Qt/CEF/SaveAs execution.
"""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_pinned_v2_supervisor as supervisor
import native_pinned_saveas_event_v2 as event_v2
import synthetic_roundtrip as ooxml
from test_native_pinned_saveas_event_v2 import SESSION, TAB, envelope
from test_synthetic_roundtrip import environment


class PinnedV2SupervisorTests(unittest.TestCase):
    def prepare(self, tmp, fixture):
        root, output, _ = environment(tmp, fixture)
        sample = output.read_bytes()
        output.unlink()
        binary = Path(tmp) / "lab-qt-host"
        binary.write_bytes(b"#!/bin/sh\nexit 0\n")
        binary.chmod(0o700)
        libs = {}
        for key in ("ascdocumentscore", "qtascdocumentscore"):
            path = Path(tmp) / ("lib" + key + ".so")
            path.write_bytes(b"\x7fELF" + key.encode())
            path.chmod(0o600)
            libs[key] = (path, hashlib.sha256(path.read_bytes()).hexdigest())
        pin = hashlib.sha256(binary.read_bytes()).hexdigest()
        return root, output, sample, binary, pin, libs

    def capture_for(self, fixture, output, sample, binary, *, mutate=None,
                    changes=None, extra=b"", exit_code=0):
        def fake(argv, env):
            self.assertEqual([str(binary),
                "--open-staged-native-saveas-event-v2-lab", str(output.parent.parent),
                fixture, SESSION, TAB], argv)
            self.assertEqual("wayland", env["QT_QPA_PLATFORM"])
            self.assertNotIn("LD_PRELOAD", env)
            self.assertNotIn("QT_PLUGIN_PATH", env)
            output.write_bytes(sample)
            if mutate:
                mutate()
            receipt = envelope()
            receipt["fixtureName"] = fixture
            receipt["receipt"]["fixtureName"] = fixture
            receipt["receipt"]["outputSha256"] = hashlib.sha256(sample).hexdigest()
            if changes:
                changes(receipt)
            return exit_code, (json.dumps(receipt, separators=(",", ":")) +
                               "\n").encode("ascii") + extra
        return fake

    def run_ok(self, tmp, fixture):
        root, output, sample, binary, pin, libs = self.prepare(tmp, fixture)
        receipt = supervisor.run_supervised_v2(
            root, fixture, SESSION, TAB, binary, pin, libs,
            approved_disposable_vm=True, env={"QT_QPA_PLATFORM": "wayland"},
            capture=self.capture_for(fixture, output, sample, binary))
        self.assertEqual(supervisor.SCHEMA, receipt["schema"])
        self.assertTrue(receipt["nativeProcessExitZero"])
        self.assertTrue(receipt["runtimeLibraryPinsMatched"])
        self.assertTrue(receipt["stdoutSingleLineBounded"])
        self.assertTrue(receipt["joinedCandidate"]["independentOoxmlCorrelated"])
        self.assertEqual(hashlib.sha256(sample).hexdigest(),
                         receipt["joinedCandidate"]["outputSha256"])
        self.assertFalse(receipt["nativeSaveProven"])
        self.assertFalse(receipt["approvedForProduction"])
        self.assertFalse(receipt["joinedCandidate"]["saveVerified"])

    def test_all_three_formats_remain_synthetic_unprivileged(self):
        for fixture in ooxml.FIXTURES:
            with self.subTest(fixture=fixture), tempfile.TemporaryDirectory() as tmp:
                self.run_ok(tmp, fixture)

    def test_prelaunch_rejects_missing_ack_invalid_pins_preexisting_output(self):
        for case in ("ack", "binary", "lib", "output", "missing_lib", "wrong_platform",
                     "preload", "libdir", "symlink"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as tmp:
                fixture = ooxml.FIXTURES[0]
                root, output, sample, binary, pin, libs = self.prepare(tmp, fixture)
                env = {"QT_QPA_PLATFORM": "wayland"}
                approval = True
                if case == "ack":
                    approval = False
                elif case == "binary":
                    pin = "0" * 64
                elif case == "lib":
                    libs["ascdocumentscore"] = (libs["ascdocumentscore"][0], "0" * 64)
                elif case == "output":
                    output.write_bytes(sample)
                elif case == "missing_lib":
                    del libs["qtascdocumentscore"]
                elif case == "wrong_platform":
                    env["QT_QPA_PLATFORM"] = "xcb"
                elif case == "preload":
                    env["LD_PRELOAD"] = "/tmp/evil.so"
                elif case == "libdir":
                    outside = root / "inside-staging.so"
                    outside.write_bytes(b"\x7fELFTEST")
                    libs["ascdocumentscore"] = (outside, hashlib.sha256(
                        outside.read_bytes()).hexdigest())
                elif case == "symlink":
                    libpath, sha = libs["qtascdocumentscore"]
                    linked = Path(tmp) / "libqtascdocumentscore-linked.so"
                    linked.symlink_to(libpath)
                    libs["qtascdocumentscore"] = (linked, sha)
                invoked = []
                with self.assertRaises(supervisor.V2SupervisorError):
                    supervisor.run_supervised_v2(
                        root, fixture, SESSION, TAB, binary, pin, libs,
                        approved_disposable_vm=approval, env=env,
                        capture=lambda *_: invoked.append(True))
                self.assertEqual([], invoked)

    def test_postlaunch_refuses_library_binary_changes_and_nonzero_exit(self):
        for case in ("lib_changed", "binary_changed", "exit_code", "stdout_junk",
                     "stdout_duplicate", "wrong_digest", "privileged"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as tmp:
                fixture = ooxml.FIXTURES[0]
                root, output, sample, binary, pin, libs = self.prepare(tmp, fixture)
                def mutate():
                    if case == "lib_changed":
                        libs["ascdocumentscore"][0].write_bytes(b"\x7fELFTAMPER")
                    elif case == "binary_changed":
                        binary.write_bytes(b"#!/bin/sh\nexit 7\n")
                def changes(receipt):
                    if case == "wrong_digest":
                        receipt["receipt"]["outputSha256"] = "0" * 64
                    if case == "privileged":
                        receipt["receipt"]["approvedForProduction"] = True
                callback = self.capture_for(
                    fixture, output, sample, binary, mutate=mutate, changes=changes,
                    exit_code=1 if case == "exit_code" else 0,
                    extra=b"abc" if case == "stdout_junk" else
                          (b"\n" if case == "stdout_duplicate" else b""))
                with self.assertRaises(supervisor.V2SupervisorError):
                    supervisor.run_supervised_v2(
                        root, fixture, SESSION, TAB, binary, pin, libs,
                        approved_disposable_vm=True, env={"QT_QPA_PLATFORM": "wayland"},
                        capture=callback)

    def test_no_downgrade_to_v1_stdout(self):
        with tempfile.TemporaryDirectory() as tmp:
            fixture = ooxml.FIXTURES[0]
            root, output, sample, binary, pin, libs = self.prepare(tmp, fixture)
            def legacy(argv, env):
                output.write_bytes(sample)
                return 0, b'{"schema":"getdoc-native-saveas-candidate-lab/1"}\n'
            with self.assertRaises(supervisor.V2SupervisorError):
                supervisor.run_supervised_v2(
                    root, fixture, SESSION, TAB, binary, pin, libs,
                    approved_disposable_vm=True, env={"QT_QPA_PLATFORM": "wayland"},
                    capture=legacy)


if __name__ == "__main__":
    unittest.main()
