"""GD31J27o deferred supervisor/UID/FD3 snapshot contracts, no actual launch.

All samples are synthetic /proc facts. A positive observed snapshot must
NEVER confer permission to connect, launch, save, publish, merge or release.
"""
from pathlib import Path
import copy
import socket
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_supervisor_handoff as handoff

STATUS = """Name:\tgetdoc-lab
Uid:\t40002\t40002\t40002\t40002
Gid:\t40002\t40002\t40002\t40002
NoNewPrivs:\t1
Seccomp:\t2
Seccomp_filters:\t1
CapInh:\t0000000000000000
CapPrm:\t0000000000000000
CapEff:\t0000000000000000
CapBnd:\t0000000000000000
CapAmb:\t0000000000000000
TracerPid:\t0
"""
MOUNTS = """23 1 0:21 / / rw,relatime - tmpfs tmpfs rw
24 23 0:22 / /run rw,nosuid,nodev - tmpfs tmpfs rw
25 23 8:2 / /usr ro,relatime - ext4 /dev/vda2 ro
"""

def synthetic():
    return {
        "schema": handoff.SNAPSHOT_SCHEMA,
        "workerPid": 22001,
        "workerUid": 40002,
        "brokerUid": 40001,
        "observedPeerPid": 22001,
        "observedPeerUid": 40002,
        "observedPeerGid": 40002,
        "workerStatus": STATUS,
        "workerMountinfo": MOUNTS,
        "workerNamespaces": {"mnt": 31001, "net": 31002},
        "brokerNamespaces": {"mnt": 41001, "net": 41002},
        "workerFdTargets": {"0": "/dev/null", "1": "pipe:[7]",
                            "2": "pipe:[8]", "3": "socket:[12345]"},
    }


class SupervisorHandoffTests(unittest.TestCase):
    def test_valid_synthetic_snapshot_requires_independent_review_only(self):
        result = handoff.inspect_snapshot(synthetic())
        self.assertEqual("requires-independent-review", result["reviewState"])
        self.assertEqual(4, result["observedWorkerFds"])
        self.assertTrue(result["observedFd3Socket"])
        for key in (
            "launchAuthorized", "kernelIsolationAttested",
            "brokerHandshakeAttested", "nativePublicationAuthorized",
            "nativeSaveProven", "documentVerified", "saveVerified",
            "approvedForProduction", "releaseReady",
            "writePermissionGranted", "safeToDeleteAutomatically",
        ):
            self.assertIs(result[key], False)

    def test_unknown_keys_and_fake_approvals_refuse(self):
        for path, value in (
            (("approval",), True),
            (("workerUid",), 0),
            (("brokerUid",), 40002),
            (("observedPeerPid",), 22002),
            (("observedPeerUid",), 40001),
            (("observedPeerGid",), 0),
            (("workerFdTargets", "4"), "socket:[99999]"),
            (("workerFdTargets", "3"), "anon_inode:[eventfd]"),
            (("workerFdTargets", "3"), "/tmp/synthetic-socket"),
            (("workerFdTargets", "0"), "socket:[12345]"),
            (("workerFdTargets", "1"), "/home/user/confidential"),
            (("workerNamespaces", "mnt"), 41001),
            (("brokerNamespaces", "net"), 31002),
        ):
            with self.subTest(path=path):
                sample = synthetic()
                target = sample
                for part in path[:-1]:
                    target = target[part]
                target[path[-1]] = value
                with self.assertRaises(handoff.HandoffError):
                    handoff.inspect_snapshot(sample)

    def test_unsafe_status_and_mounts_refused(self):
        for status, mounts in (
            (STATUS.replace("Seccomp:\t2", "Seccomp:\t0"), MOUNTS),
            (STATUS.replace("CapEff:\t0000000000000000",
                            "CapEff:\t0000000000000001"), MOUNTS),
            (STATUS, MOUNTS + "26 23 0:40 / /mnt/share rw - cifs server:/public rw\n"),
        ):
            with self.subTest(status=status[-30:], mounts=mounts[-30:]):
                sample = synthetic()
                sample["workerStatus"] = status
                sample["workerMountinfo"] = mounts
                with self.assertRaises(handoff.HandoffError):
                    handoff.inspect_snapshot(sample)

    def test_untrusted_socket_is_not_credentials_attestation(self):
        class FakeSock:
            family = socket.AF_UNIX
            type = socket.SOCK_SEQPACKET
            def getsockopt(self, *args):
                import struct
                return struct.pack("3i", 11111, 40001, 40001)
        with self.assertRaises(handoff.HandoffError):
            handoff.read_actual_handoff(FakeSock(),
                                        expected_worker_pid=22001,
                                        expected_worker_uid=40002,
                                        broker_uid=40001)

    def test_snapshot_does_not_override_external_profile_or_create_process(self):
        self.assertEqual("blocked", handoff.default_handoff_gate()["state"])
        self.assertFalse(handoff.default_handoff_gate()["launchAuthorized"])
        self.assertFalse(handoff.default_handoff_gate()["nativePublicationAuthorized"])


if __name__ == "__main__":
    unittest.main()
