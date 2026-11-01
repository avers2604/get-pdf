"""GD31J27m deferred source contracts: no privilege escalation from broker.

Tests are written but not executed; source scans cannot attest real Linux
isolation, SELinux/AppArmor, seccomp, mount policies or a native SDK.
"""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]


class BrokerHandoffSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.broker = (LAB / "native_saveas_fd_broker.py").read_text("utf-8")
        cls.profile = (LAB / "native_saveas_broker_profile.py").read_text("utf-8")
        cls.worker = (LAB / "native_saveas_fd_worker.py").read_text("utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf-8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf-8")
        cls.acceptance = (ROOT / "scripts/office_acceptance.py").read_text("utf-8")

    def test_broker_only_transfers_three_readonly_fds_to_exact_peer(self):
        for token in (
            "class BrokerSession", "self.consumed = True",
            "socket.AF_UNIX", "socket.SOCK_SEQPACKET", "socket.SO_PEERCRED",
            "socket.SCM_RIGHTS", "socket.MSG_TRUNC", "socket.MSG_CTRUNC",
            "sock.sendmsg([packet]", "sock.recvmsg(MAX_REPLY_BYTES + 1, 0)",
            "expected_worker_uid == broker_uid", "expected_worker_uid <= 0",
            "broker_uid <= 0", "broker_uid != os.geteuid()",
            "worker.parse_request(packet)", "approved_fixture_hashes",
            'request["sourceSha256"] != approved_fixture_hashes[request["fixtureName"]]',
            "fcntl.F_GETFD",
            "fcntl.FD_CLOEXEC", "os.O_RDONLY", "sock.settimeout(5.0)",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.broker)
        for forbidden in (
            "subprocess", "os.open(", "os.link(", "os.unlink(", "os.chmod(",
            "os.chown(", "os.setuid(", "os.setgid(", "os.unshare(",
            "os.replace(", "os.rename(", "os.write(", "os.fork(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.broker)

    def test_no_worker_or_broker_grants_publishing_rights(self):
        for code in (self.worker, self.broker):
            for field in ('"nativeSaveProven": False',
                          '"documentVerified": False',
                          '"saveVerified": False',
                          '"approvedForProduction": False',
                          '"writePermissionGranted": False',
                          '"releaseReady": False'):
                self.assertIn(field, code)
            self.assertIn('"state": "denied"', code)
        self.assertIn('"publishOperation": "disabled"', self.profile)
        self.assertIn('"nativePublicationAuthorized": False', self.profile)
        self.assertIn('"kernelIsolationAttested": False', self.profile)

    def test_cmake_worker_and_broker_are_both_blocked_and_unwired(self):
        self.assertIn('GETDOC_GD31J27M_ENABLE_BROKER_PUBLICATION_LAB', self.cmake)
        self.assertIn('GD31J27M_BLOCKED:', self.cmake)
        self.assertIn('GD31J27L_BLOCKED:', self.cmake)
        self.assertIn("NativeSaveAsPublish(", self.policy)
        self.assertNotIn("native_saveas_fd_broker.py", self.policy)
        self.assertNotIn("native_saveas_fd_broker.py", self.cmake)
        self.assertIn("native-saveas-fd-broker-source-contract-tests",
                      self.acceptance)


if __name__ == "__main__":
    unittest.main()
