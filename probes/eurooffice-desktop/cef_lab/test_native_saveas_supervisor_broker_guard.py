"""GD31J27o: one-shot broker must observe kernel FD3 before handoff.

All tests are deferred synthetic fixtures; mocking observer is intentionally
only for negative state-machine/source tests, not sandbox proof.
"""
from pathlib import Path
import json
import os
import socket
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_fd_broker as broker
import native_saveas_fd_worker as worker
import native_saveas_supervisor_handoff as observer

GOOD = b"GD31J27O_SAFELY_PINNED"
import hashlib
DIGEST = hashlib.sha256(GOOD).hexdigest()


def pins():
    return {
        "gd31j6_writer.docx": DIGEST,
        "gd31j6_calc.xlsx": "a" * 64,
        "gd31j6_impress.pptx": "b" * 64,
    }


def request():
    return (json.dumps({
        "schema": worker.REQUEST_SCHEMA,
        "operation": "inspect_only",
        "fixtureName": "gd31j6_writer.docx",
        "sessionId": "102030405060708090a0b0c0d0e0f010",
        "tabId": "f010e0d0c0b0a0908070605040302010",
        "sourceSha256": DIGEST,
        "expectedFinalName": "gd31j25_writer_saved.docx",
        "laboratoryOnly": True,
    }, separators=(",", ":")) + "\n").encode("ascii")


@unittest.skipUnless(sys.platform.startswith("linux") and os.geteuid() != 0,
                     "Unprivileged Linux sandbox broker only")
class SupervisorBrokerGuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        source = root / "gd31j6_writer.docx"
        source.write_bytes(GOOD)
        scratch = root / "scratch"
        output = root / "output"
        scratch.mkdir(mode=0o700)
        output.mkdir(mode=0o700)
        self.fds = [
            os.open(source, os.O_RDONLY | os.O_CLOEXEC),
            os.open(scratch, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC),
            os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC),
        ]

    def tearDown(self):
        for fd in self.fds:
            os.close(fd)
        self.tmp.cleanup()

    def _sock(self):
        from test_native_saveas_fd_broker import FakeSocket
        return FakeSocket(os.geteuid() + 10001, pid=22001)

    def _call(self, session, sock):
        return broker.handoff_after_kernel_review(
            session, sock, request(), self.fds,
            broker_uid=os.geteuid(),
            expected_worker_uid=os.geteuid() + 10001,
            expected_worker_pid=22001,
            expected_worker_start_ticks=123456,
            approved_disposable_vm=True,
            approved_fixture_hashes=pins(),
        )

    def test_without_observed_kernel_fd3_denies_before_sending(self):
        session = broker.BrokerSession()
        sock = self._sock()
        with mock.patch.object(
            broker.supervisor_handoff, "read_actual_handoff",
            side_effect=observer.HandoffError("unobservable procfs")
        ), mock.patch.object(broker.pidfd,"pin_worker_process"):
            with self.assertRaises(broker.BrokerError):
                self._call(session, sock)
        self.assertTrue(session.consumed)
        self.assertEqual([], sock.sent)

    def test_synthetic_positive_observation_is_still_only_deny_response(self):
        session = broker.BrokerSession()
        sock = self._sock()
        report = observer.default_handoff_gate()
        report.update({"observedWorkerFds": 4, "observedFd3Socket": True})
        with mock.patch.object(
            broker.supervisor_handoff, "read_actual_handoff",
            return_value=report
        ), mock.patch.object(broker.pidfd,"pin_worker_process"):
            result = self._call(session, sock)
        self.assertEqual("denied", result["state"])
        self.assertFalse(result["launchAuthorized"])
        self.assertFalse(result["nativePublicationAuthorized"])
        self.assertFalse(result["releaseReady"])
        self.assertEqual(1, len(sock.sent))

    def test_unexpected_preflight_state_is_not_transfer_authority(self):
        session = broker.BrokerSession()
        sock = self._sock()
        forged = observer.default_handoff_gate()
        forged.update({"observedWorkerFds": 4, "observedFd3Socket": True,
                       "launchAuthorized": True})
        with mock.patch.object(
            broker.supervisor_handoff, "read_actual_handoff",
            return_value=forged
        ), mock.patch.object(broker.pidfd,"pin_worker_process"):
            with self.assertRaises(broker.BrokerError):
                self._call(session, sock)
        self.assertEqual([], sock.sent)


if __name__ == "__main__":
    unittest.main()
