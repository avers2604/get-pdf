"""GD31J27l: static invariants for separate deny-only SCM_RIGHTS worker.

Do not accidentally wire the worker into the production UI or reinterpret
its denial response as native SaveAs proof. Deferred execution.
"""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]

class NativeFdWorkerSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.worker = (LAB / "native_saveas_fd_worker.py").read_text("utf-8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf-8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf-8")
        cls.acceptance = (ROOT / "scripts/office_acceptance.py").read_text("utf-8")

    def test_only_fd_3_unix_seqpacket_and_three_exact_received_fds(self):
        for token in (
            "socket.socket(fileno=3)", "socket.AF_UNIX",
            "socket.SOCK_SEQPACKET", "socket.SO_PEERCRED",
            "socket.SCM_RIGHTS", "socket.MSG_CMSG_CLOEXEC",
            "socket.MSG_CTRUNC", "socket.MSG_TRUNC",
            "ROLE_COUNT = 3", "os.O_NOFOLLOW", "fcntl.F_GETFD",
            "inspect_fds(received, request", "sock.settimeout(5.0)",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.worker)

    def test_worker_must_refuse_same_uid_caps_network_or_root(self):
        for token in (
            '"NoNewPrivs"', '"Seccomp"', '"CapEff"',
            "worker_uid <= 0", "peer_uid == worker_uid",
            "worker_namespaces", "peer_namespaces",
            "/proc/self/ns/", "/proc/{peer_pid}/ns/",
            "getdoc-gd31j27l-native-fd-worker-inspect-lab/1",
            "exactly three", "inspect_only",
        ):
            if token == "exactly three":
                self.assertIn("Exactly three", self.worker)
            else:
                self.assertIn(token, self.worker)

    def test_no_publish_or_privileged_launch_or_production_authorization(self):
        for forbidden in (
            "subprocess", "os.unlink(", "os.remove(", "os.rename(",
            "os.replace(", "os.link(", "os.chmod(", "os.chown(",
            "setuid(", "setgid(", "execv(", "Popen(", "O_CREAT",
            "write_user_file(", "CAP_SETUID",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.worker)
        for token in (
            '"nativeSaveProven": False', '"documentVerified": False',
            '"saveVerified": False', '"approvedForProduction": False',
            '"writePermissionGranted": False', '"releaseReady": False',
            '"state": "denied"', "return 2",
        ):
            self.assertIn(token, self.worker)

    def test_existing_lab_remains_off_by_default_and_legacy_only(self):
        self.assertIn(
            'option(GETDOC_GD31J26_ENABLE_NATIVE_SAVEAS_LAB '
            '"Isolated synthetic SaveAs via exact vendor event" OFF)',
            self.cmake)
        self.assertIn('option(GETDOC_GD31J27L_ENABLE_ISOLATED_WORKER_LAB', self.cmake)
        self.assertIn('message(FATAL_ERROR "GD31J27L_BLOCKED:', self.cmake)
        self.assertIn("NativeSaveAsPublish(", self.policy)
        self.assertNotIn("native_saveas_fd_worker.py", self.policy)
        self.assertNotIn("native_saveas_fd_worker.py", self.cmake)
        self.assertIn("native-saveas-fd-worker-source-contract-tests",
                      self.acceptance)


if __name__ == "__main__":
    unittest.main()
