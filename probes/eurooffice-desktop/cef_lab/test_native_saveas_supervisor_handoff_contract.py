"""GD31J27o source contracts: observation gates a deny-only broker, no launcher.

Deferred source check; these text invariants do not prove working OS sandbox.
"""
from pathlib import Path
import unittest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


class SupervisorSourceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.observer = (HERE / "native_saveas_supervisor_handoff.py").read_text("utf8")
        cls.broker = (HERE / "native_saveas_fd_broker.py").read_text("utf8")
        cls.worker = (HERE / "native_saveas_fd_worker.py").read_text("utf8")
        cls.unit = (HERE / "getdoc-gd31j27o-supervisor.blocked.service").read_text("utf8")
        cls.cmake = (HERE / "CMakeLists.txt").read_text("utf8")
        cls.plan = (ROOT / "scripts/office_acceptance.py").read_text("utf8")

    def test_kernel_provenance_is_observed_before_broker_scm_rights(self):
        for token in (
            "def read_actual_handoff(", "socket.SO_PEERCRED",
            "expected_worker_start_ticks", "_proc_start_ticks(pid)",
            'os.scandir(directory)', '"/proc/{pid}/fd"',
            "observedWorkerFds", "observedFd3Socket",
            "kernel_observer.inspect_observed(", "workerFdTargets",
            "SOCKET_TARGET", "PIPE_TARGET",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.observer)
        self.assertIn("def handoff_after_kernel_review(", self.broker)
        self.assertIn("supervisor_handoff.read_actual_handoff(", self.broker)
        self.assertIn("result = session.handoff(", self.broker)
        self.assertLess(self.broker.index("supervisor_handoff.read_actual_handoff("),
                        self.broker.index("result = session.handoff("))
        self.assertIn("session.consumed = True", self.broker)
        self.assertIn("sock.sendmsg([packet]", self.broker)

    def test_every_observed_snapshot_is_denied_for_production(self):
        for key in (
            '"launchAuthorized": False',
            '"kernelIsolationAttested": False',
            '"brokerHandshakeAttested": False',
            '"nativePublicationAuthorized": False',
            '"nativeSaveProven": False',
            '"documentVerified": False',
            '"saveVerified": False',
            '"approvedForProduction": False',
            '"releaseReady": False',
            '"writePermissionGranted": False',
            '"safeToDeleteAutomatically": False',
        ):
            self.assertIn(key, self.observer)
        for banned in (
            "subprocess.", "os.setuid(", "os.setgid(", "os.chmod(",
            "os.link(", "os.unlink(", "os.rename(", "os.replace(",
            "os.mount(", "os.unshare(", "os.fork(", "os.exec",
        ):
            self.assertNotIn(banned, self.observer)

    def test_systemd_specimen_and_feature_flag_cannot_enable_publication(self):
        self.assertIn("RefuseManualStart=yes", self.unit)
        self.assertIn("ExecStart=/usr/bin/false", self.unit)
        self.assertIn("PrivateNetwork=yes", self.unit)
        self.assertIn("PrivateMounts=yes", self.unit)
        self.assertIn("ProtectSystem=strict", self.unit)
        self.assertIn("NoNewPrivileges=yes", self.unit)
        self.assertIn("CapabilityBoundingSet=", self.unit)
        self.assertIn("RestrictAddressFamilies=AF_UNIX", self.unit)
        self.assertNotIn("WantedBy=", self.unit)
        self.assertIn("GETDOC_GD31J27O_ENABLE_SUPERVISOR_PUBLICATION_LAB", self.cmake)
        self.assertIn("GD31J27O_BLOCKED:", self.cmake)
        self.assertNotIn("native_saveas_supervisor_handoff.py", self.cmake)
        self.assertIn("native-saveas-supervisor-handoff-source-tests", self.plan)
        self.assertIn("return 2", self.worker)


if __name__ == "__main__":
    unittest.main()
