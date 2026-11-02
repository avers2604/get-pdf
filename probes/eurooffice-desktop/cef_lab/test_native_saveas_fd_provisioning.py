"""GD31J27r: deferred, synthetic-only trusted-supervisor FD relay tests.

No separate UID is created; this is NOT OS attestation. Run later on
approved disposable offline Astra/Linux VM, never on real office files.
"""
import array
import hashlib
import json
import os
from pathlib import Path
import socket
import stat
import struct
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_fd_provisioning as provision
import native_saveas_fd_worker as worker

PAYLOAD = b"GD31J27R_SYNTHETIC_SOURCE"
SHA = hashlib.sha256(PAYLOAD).hexdigest()


def packet():
    return (json.dumps({
        "schema": worker.REQUEST_SCHEMA, "operation": "inspect_only",
        "fixtureName": "gd31j6_writer.docx",
        "expectedFinalName": "gd31j25_writer_saved.docx",
        "sourceSha256": SHA,
        "sessionId": "102030405060708090a0b0c0d0e0f010",
        "tabId": "f010e0d0c0b0a0908070605040302010",
        "laboratoryOnly": True,
    }, separators=(",", ":")) + "\n").encode("ascii")


def pins():
    return {"gd31j6_writer.docx": SHA, "gd31j6_calc.xlsx": "a"*64,
            "gd31j6_impress.pptx": "b"*64}


class SupervisorChannel:
    family = socket.AF_UNIX
    type = socket.SOCK_SEQPACKET
    def __init__(self, payload, handles, *, uid=0, pid=777, flags=0):
        self.payload = payload
        self.handles = handles
        self.uid = uid
        self.pid = pid
        self.flags = flags
        self.receives = 0
        self.timeout = None

    def getsockopt(self, level, kind, size):
        return struct.pack("3i", self.pid, self.uid, 0)

    def settimeout(self, seconds):
        self.timeout = seconds

    def recvmsg(self, size, control_size, flags):
        self.receives += 1
        # Kernel transfers duplicated owned fds, never borrowed test slots.
        transferred = [os.dup(fd) for fd in self.handles]
        ancillary = [(socket.SOL_SOCKET, socket.SCM_RIGHTS,
                      array.array("i", transferred).tobytes())]
        return self.payload, ancillary, self.flags, None


@unittest.skipUnless(sys.platform.startswith("linux"),
                     "SCM_RIGHTS Linux-only test fixture")
class TrustedFdProvisioningTests(unittest.TestCase):
    def setUp(self):
        if os.geteuid() == 0:
            self.skipTest("Unprivileged synthetic worker UID required")
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.source = root / "gd31j6_writer.docx"
        self.source.write_bytes(PAYLOAD)
        self.scratch = root / "gd31j26-private-ABCDEF"
        self.output = root / "gd31j25-output"
        self.scratch.mkdir(mode=0o700)
        self.output.mkdir(mode=0o700)
        self.handles = (
            os.open(self.source, os.O_RDONLY | os.O_CLOEXEC),
            os.open(self.scratch, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC),
            os.open(self.output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC),
        )

    def tearDown(self):
        for fd in self.handles:
            os.close(fd)
        self.tmp.cleanup()

    def test_exact_preopened_roles_allow_worker_owned_0700_but_never_publish(self):
        req = worker.parse_request(packet())
        result = provision.verify_pinned_handles(
            self.handles, req, worker_uid=os.geteuid(),
            independent_fixture_hashes=pins())
        self.assertEqual(SHA, result["sourceSha256"])
        self.assertEqual("inspect_only", result["operation"])
        self.assertIs(result["writePermissionGranted"], False)
        self.assertEqual("worker-owned-preopened", result["roleOwnership"])

    def test_no_source_sha_self_attestation_and_no_writable_fd(self):
        req = worker.parse_request(packet())
        with self.assertRaises(provision.ProvisioningError):
            provision.verify_pinned_handles(
                self.handles, req, worker_uid=os.geteuid(),
                independent_fixture_hashes={**pins(), "gd31j6_writer.docx":"c"*64})
        writer = os.open(self.source, os.O_RDWR | os.O_CLOEXEC)
        try:
            with self.assertRaises(provision.ProvisioningError):
                provision.verify_pinned_handles(
                    (writer, *self.handles[1:]), req,
                    worker_uid=os.geteuid(), independent_fixture_hashes=pins())
        finally:
            os.close(writer)

    def test_unapproved_dir_mode_or_owner_and_eexist_refused(self):
        req = worker.parse_request(packet())
        self.scratch.chmod(0o750)
        with self.assertRaises(provision.ProvisioningError):
            provision.verify_pinned_handles(
                self.handles, req, worker_uid=os.geteuid(),
                independent_fixture_hashes=pins())
        self.scratch.chmod(0o700)
        (self.output / "gd31j25_writer_saved.docx").write_bytes(b"DO_NOT_REPLACE")
        with self.assertRaises(provision.ProvisioningError):
            provision.verify_pinned_handles(
                self.handles, req, worker_uid=os.geteuid(),
                independent_fixture_hashes=pins())
        self.assertEqual(
            b"DO_NOT_REPLACE",
            (self.output/"gd31j25_writer_saved.docx").read_bytes())

    def test_supervisor_exact_fds_close_on_denied_post_receive(self):
        channel = SupervisorChannel(packet(), self.handles, uid=0)
        # Do NOT call relay: real worker UID and verified sandbox are absent.
        packet_bytes, received = provision.receive_pinned_roles(
            channel, trusted_supervisor_uid=0,
            expected_supervisor_pid=777,
            worker_uid=os.geteuid(),
            independent_fixture_hashes=pins(),
            approved_disposable_vm=True)
        try:
            self.assertEqual(packet(), packet_bytes)
            self.assertEqual(3, len(received))
        finally:
            provision.close_received_fds(received)
        self.assertEqual(1, channel.receives)
        self.assertLessEqual(channel.timeout, 5)

    def test_wrong_supervisor_uid_truncation_or_fd_overflow_refuses(self):
        for uid, flags, extras in (
            (10001, 0, ()),
            (0, socket.MSG_CTRUNC, ()),
            (0, 0, (self.handles[0],)),
        ):
            with self.subTest(uid=uid, flags=flags, extras=extras):
                channel = SupervisorChannel(
                    packet(), self.handles + extras, uid=uid, flags=flags)
                with self.assertRaises(provision.ProvisioningError):
                    provision.receive_pinned_roles(
                        channel, trusted_supervisor_uid=0,
                        expected_supervisor_pid=777,
                        worker_uid=os.geteuid(),
                        independent_fixture_hashes=pins(),
                        approved_disposable_vm=True)

    def test_relay_must_consume_session_and_never_grant_on_bad_supervisor(self):
        import native_saveas_fd_broker as broker
        session = broker.BrokerSession()
        channel = SupervisorChannel(packet(), self.handles, uid=10001)
        with self.assertRaises(provision.ProvisioningError):
            provision.relay_pinned_once(
                session, channel, object(), trusted_supervisor_uid=0,
                expected_supervisor_pid=777,
                worker_uid=os.geteuid(),
                broker_uid=os.geteuid() + 10,
                expected_worker_pid=22001,
                expected_worker_start_ticks=44001,
                approved_disposable_vm=True,
                independent_fixture_hashes=pins())
        self.assertTrue(session.consumed)


if __name__ == "__main__":
    unittest.main()
