"""GD31J27r: deferred security source contract for root-to-broker-to-worker FDs.

Only offline source inspection. Does NOT prove systemd root supervisor,
SO_PEERCRED kernel attestation, OS credentials, actual SCM_RIGHTS or SaveAs.
"""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]


class SupervisorFdRelaySourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.provision = (LAB / "native_saveas_fd_provisioning.py").read_text("utf8")
        cls.worker = (LAB / "native_saveas_fd_worker.py").read_text("utf8")
        cls.listener = (LAB / "native_saveas_broker_listener.py").read_text("utf8")
        cls.snapshot = (LAB / "native_saveas_worker_snapshot.py").read_text("utf8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf8")
        cls.acceptance = (ROOT / "scripts/office_acceptance.py").read_text("utf8")

    def test_verified_root_supervisor_and_exact_3_worker_owned_capabilities(self):
        for word in (
            "socket.SO_PEERCRED", "trusted_supervisor_uid != 0",
            "pid != expected_supervisor_pid",
            "socket.SCM_RIGHTS", "socket.MSG_CMSG_CLOEXEC",
            "socket.MSG_CTRUNC", "socket.MSG_TRUNC",
            "socket.CMSG_SPACE(ROLE_COUNT * transferred.itemsize)",
            "close_received_fds(transferred)", "close_received_fds(handles)",
            "meta.st_uid != worker_uid", "(meta.st_mode & 0o777) != 0o700",
            "worker._digest_open_fd(handles[0], identities[0])",
            'request["sourceSha256"] != independent_fixture_hashes[request["fixtureName"]]',
            "broker.handoff_after_kernel_review(", "session.consumed = True",
        ):
            with self.subTest(word=word):
                self.assertIn(word, self.provision)

    def test_worker_refuses_outside_fixture_and_same_name_symlink(self):
        self.assertIn('os.stat(request["fixtureName"], dir_fd=scratch', self.worker)
        self.assertIn("follow_symlinks=False", self.worker)
        self.assertIn("meta.st_uid != euid", self.worker)
        self.assertIn("Synthetic fixture name replaced during inspection", self.worker)
        self.assertIn("Synthetic source does not match exact fixture name", self.worker)

    def test_no_extra_permissions_no_unreviewed_publisher_connection(self):
        for forbidden in (
            "subprocess", "os.setuid(", "os.setgid(", "os.chown(",
            "os.chmod(", "os.unlink(", "os.rename(", "os.replace(",
            "os.link(", "os.fork(", "os.execv(", "os.open(",
            "publish_disposable_fixture(",
        ):
            self.assertNotIn(forbidden, self.provision)
        self.assertNotIn("native_saveas_fd_provisioning", self.listener)
        self.assertNotIn("native_saveas_fd_provisioning", self.snapshot)
        self.assertNotIn("native_saveas_fd_provisioning", self.cmake)
        for claim in (
            '"state": "denied"', '"trustedSupervisorAttested": False',
            '"nativePublicationAuthorized": False',
            '"writePermissionGranted": False',
            '"releaseReady": False', '"approvedForProduction": False',
        ):
            self.assertIn(claim, self.provision)
        self.assertIn("GD31J27R_BLOCKED:", self.cmake)
        self.assertIn("native-saveas-fd-provisioning-source-tests", self.acceptance)


if __name__ == "__main__":
    unittest.main()
