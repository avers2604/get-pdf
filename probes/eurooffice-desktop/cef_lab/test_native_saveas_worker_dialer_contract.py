"""GD31J27p source contracts: genuine post-UID socket and disabled service.

These source tests are deferred. The concrete runtime still lacks a broker
listener/provisioning, and must not be treated as an installed service.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]


class PostUidDialerSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dialer=(LAB/"native_saveas_worker_dialer.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.unit=(LAB/"getdoc-gd31j27p-worker.blocked.service").read_text("utf8")
        cls.config=(LAB/"getdoc-gd31j27p-worker-policy.example.json").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.plan=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_exact_root_pinned_policy_and_broker_socket_post_uid(self):
        for phrase in (
            'FIXED_CONFIG = "/etc/getdoc-lab/gd31j27p-offline-worker.json"',
            'FIXED_SOCKET = "/run/getdoc-gd31j27p-disposable/worker.sock"',
            "validate_policy_file_meta(", "worker_identity_matches(cfg)",
            "os.getuid() == os.geteuid()", "os.getgid() == os.getegid()",
            "socket.AF_UNIX", "socket.SOCK_SEQPACKET",
            "socket.SO_PEERCRED", "channel.connect(FIXED_SOCKET)",
            "channel.fileno() != 3", "_only_expected_fds({0, 1, 2, 3})",
            "worker.serve_one(channel)", "os.O_NOFOLLOW",
            '"publishEnabled"] is not False',
        ):
            with self.subTest(phrase=phrase):
                self.assertIn(phrase, self.dialer)

    def test_no_namespace_or_uid_manipulation_and_no_publication(self):
        for phrase in (
            "subprocess", "os.setuid(", "os.setgid(",
            "os.chown(", "os.chmod(", "os.link(", "os.rename(",
            "os.replace(", "os.unlink(", "os.fork(", "os.unshare(",
            "os.write(", "socket.socketpair(", "os.execv(",
        ):
            with self.subTest(phrase=phrase):
                self.assertNotIn(phrase, self.dialer)
        for phrase in (
            '"launchAuthorized": False', '"brokerHandshakeAttested": False',
            '"nativePublicationAuthorized": False', '"nativeSaveProven": False',
            '"documentVerified": False', '"saveVerified": False',
            '"approvedForProduction": False', '"releaseReady": False',
            '"writePermissionGranted": False',
        ):
            self.assertIn(phrase, self.dialer)
        self.assertIn("return 2", self.worker)

    def test_service_references_real_dialer_but_stays_unstartable(self):
        for phrase in (
            "User=getdoc-lab-saveas-worker",
            "Group=getdoc-lab-ipc", "RefuseManualStart=yes",
            "ConditionPathExists=", "PrivateNetwork=yes",
            "PrivateMounts=yes", "NoNewPrivileges=yes",
            "ProtectSystem=strict", "RestrictAddressFamilies=AF_UNIX",
            "CapabilityBoundingSet=", "SystemCallFilter=@system-service",
            "ExecStart=/usr/bin/python3 -B /opt/getdoc-lab/cef_lab/native_saveas_worker_dialer.py",
        ):
            self.assertIn(phrase, self.unit)
        self.assertNotIn("WantedBy=", self.unit)
        self.assertNotIn("ExecStart=/usr/bin/false", self.unit)
        self.assertIn('"publishEnabled":false', self.config)
        self.assertIn('GD31J27P_BLOCKED:', self.cmake)
        self.assertIn("native-saveas-post-uid-dialer-source-tests", self.plan)


if __name__=="__main__":
    unittest.main()
