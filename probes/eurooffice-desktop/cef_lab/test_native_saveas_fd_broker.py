"""GD31J27m: deferred tests of a one-shot FD broker boundary.

These use a fake SOCK_SEQPACKET transport and only synthetic tempfile fds.
No external worker, UID changes, CEF, SDK, deployment, or real test execution.
"""
import array
import hashlib
import json
import os
from pathlib import Path
import socket
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_fd_worker as worker
import native_saveas_fd_broker as broker

FIXTURE = "gd31j6_writer.docx"
PAYLOAD = b"GETDOC-GD31J27M-SYNTHETIC-INSPECT"
SESSION = "102030405060708090a0b0c0d0e0f010"
TAB = "f010e0d0c0b0a0908070605040302010"


def pins():
    # Externally pinned trusted fixture hashes, NOT derived from the request.
    return {
        "gd31j6_writer.docx": hashlib.sha256(PAYLOAD).hexdigest(),
        "gd31j6_calc.xlsx": "a" * 64,
        "gd31j6_impress.pptx": "b" * 64,
    }


def request():
    return (json.dumps({
        "schema": worker.REQUEST_SCHEMA,
        "operation": "inspect_only",
        "fixtureName": FIXTURE,
        "sessionId": SESSION,
        "tabId": TAB,
        "sourceSha256": hashlib.sha256(PAYLOAD).hexdigest(),
        "expectedFinalName": "gd31j25_writer_saved.docx",
        "laboratoryOnly": True,
    }, separators=(",", ":")) + "\n").encode("ascii")


class FakeSocket:
    family = socket.AF_UNIX
    type = socket.SOCK_SEQPACKET
    def __init__(self, uid, *, pid=8080, reply=None, flags=0):
        self.uid = uid
        self.pid = pid
        self.sent = []
        self.flags = flags
        self.timeout = None
        self.reply = reply if reply is not None else (
            b'{"state":"denied","laboratoryOnly":true}\n')

    def getsockopt(self, level, name, size):
        assert (level, name, size) == (
            socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
        return struct.pack("3i", self.pid, self.uid, 200)

    def settimeout(self, seconds):
        self.timeout = seconds

    def sendmsg(self, buffers, ancillary, flags=0):
        self.sent.append((buffers, ancillary, flags))
        return sum(map(len, buffers))

    def recvmsg(self, length, control_bytes=0, flags=0):
        return self.reply, [], self.flags, None


@unittest.skipUnless(sys.platform.startswith("linux"),
                     "Linux kernel credential and FD broker")
class BrokerHandoffTests(unittest.TestCase):
    def setUp(self):
        if os.geteuid() == 0:
            self.skipTest("Unprivileged broker tests must never run as root")
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.source = root / FIXTURE
        self.source.write_bytes(PAYLOAD)
        self.scratch = root / "gd31j26-private-ABCDEF"
        self.output = root / "gd31j25-output"
        self.scratch.mkdir(mode=0o700)
        self.output.mkdir(mode=0o700)
        self.fds = (
            os.open(self.source, os.O_RDONLY | os.O_CLOEXEC),
            os.open(self.scratch, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC),
            os.open(self.output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC),
        )

    def tearDown(self):
        for fd in self.fds:
            os.close(fd)
        self.temp.cleanup()

    def test_one_shot_handoff_transmits_exactly_three_fds_and_never_grants(self):
        uid = os.geteuid()
        peer_uid = uid + 10001 if uid != 0 else 40001
        sock = FakeSocket(peer_uid)
        session = broker.BrokerSession()
        response = session.handoff(sock, request(), self.fds,
                                   broker_uid=uid, expected_worker_uid=peer_uid,
                                   approved_disposable_vm=True,
                                   approved_fixture_hashes=pins())
        self.assertTrue(session.consumed)
        self.assertEqual("denied", response["state"])
        self.assertFalse(response["writePermissionGranted"])
        self.assertFalse(response["releaseReady"])
        self.assertEqual(1, len(sock.sent))
        buffers, ancillary, _ = sock.sent[0]
        self.assertEqual([request()], buffers)
        self.assertEqual(1, len(ancillary))
        level, kind, raw = ancillary[0]
        self.assertEqual((socket.SOL_SOCKET, socket.SCM_RIGHTS), (level, kind))
        self.assertEqual(list(self.fds), list(array.array("i", raw)))
        self.assertLessEqual(sock.timeout, 5.0)
        with self.assertRaises(broker.BrokerError):
            session.handoff(sock, request(), self.fds,
                            broker_uid=uid, expected_worker_uid=peer_uid,
                            approved_disposable_vm=True,
                                   approved_fixture_hashes=pins())

    def test_missing_vm_approval_or_same_uid_peer_refuses_before_sending(self):
        uid = os.geteuid()
        for approved, peer_uid in ((False, uid+20001), (True, uid),
                                   (True, 0)):
            with self.subTest(approved=approved, peer_uid=peer_uid):
                sock = FakeSocket(peer_uid)
                with self.assertRaises(broker.BrokerError):
                    broker.BrokerSession().handoff(
                        sock, request(), self.fds, broker_uid=uid,
                        expected_worker_uid=peer_uid,
                        approved_disposable_vm=approved,
                        approved_fixture_hashes=pins())
                self.assertEqual([], sock.sent)

    def test_request_sha_must_match_external_supervisor_pin(self):
        uid = os.geteuid()
        peer_uid = uid + 10001
        forged = request().replace(
            hashlib.sha256(PAYLOAD).hexdigest().encode("ascii"), b"c" * 64)
        sock = FakeSocket(peer_uid)
        with self.assertRaises(broker.BrokerError):
            broker.BrokerSession().handoff(
                sock, forged, self.fds,
                broker_uid=uid, expected_worker_uid=peer_uid,
                approved_disposable_vm=True, approved_fixture_hashes=pins())
        self.assertEqual([], sock.sent)

    def test_unexpected_peer_and_forged_worker_success_always_refused(self):
        uid = os.geteuid()
        target = uid + 20001
        for sock in (
            FakeSocket(target + 1),
            FakeSocket(target, pid=0),
            FakeSocket(target, reply=b'{"state":"approved"}\n'),
            FakeSocket(target, reply=b'{"state":"denied","laboratoryOnly":false}\n'),
            FakeSocket(target, reply=b'{"state":"denied","laboratoryOnly":true}\nX'),
            FakeSocket(target, flags=getattr(socket, "MSG_TRUNC", 32)),
        ):
            with self.subTest(sock=sock):
                with self.assertRaises(broker.BrokerError):
                    broker.BrokerSession().handoff(
                        sock, request(), self.fds, broker_uid=uid,
                        expected_worker_uid=target,
                        approved_disposable_vm=True,
                                   approved_fixture_hashes=pins())

    def test_reject_writer_source_duplicate_fds_and_non_synthetic_operation(self):
        uid = os.geteuid()
        target = uid + 20001
        wrong = request().replace(b'"inspect_only"', b'"publish"')
        wfd = os.open(self.source, os.O_RDWR | os.O_CLOEXEC)
        try:
            for payload, handles in ((wrong, self.fds),
                                     (request(), (wfd, *self.fds[1:])),
                                     (request(), (self.fds[0], self.fds[0], self.fds[2]))):
                sock = FakeSocket(target)
                with self.assertRaises(broker.BrokerError):
                    broker.BrokerSession().handoff(
                        sock, payload, handles, broker_uid=uid,
                        expected_worker_uid=target,
                        approved_disposable_vm=True,
                                   approved_fixture_hashes=pins())
                self.assertEqual([], sock.sent)
        finally:
            os.close(wfd)


if __name__ == "__main__":
    unittest.main()
