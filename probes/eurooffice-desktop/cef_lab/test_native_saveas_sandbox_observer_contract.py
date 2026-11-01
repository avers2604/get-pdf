"""GD31J27n: deferred source gate prevents falsely enabling blocked sandbox."""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]


class SandboxObserverSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.obs = (LAB / "native_saveas_sandbox_observer.py").read_text("utf8")
        cls.worker = (LAB / "native_saveas_fd_worker.py").read_text("utf8")
        cls.unit = (LAB / "getdoc-gd31j27n-worker.blocked.service").read_text("utf8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf8")
        cls.acceptance = (ROOT / "scripts/office_acceptance.py").read_text("utf8")

    def test_live_observer_runs_before_receiving_rights(self):
        self.assertIn("kernel_observer.inspect_current(peer_pid=pid, peer_uid=peer_uid)", self.worker)
        self.assertLess(
            self.worker.index("kernel_observer.inspect_current(peer_pid=pid, peer_uid=peer_uid)"),
            self.worker.index("payload, received = _receive_exact_three(sock)"))
        for item in (
            "/proc/self/status", "/proc/self/mountinfo", "/proc/self/ns/",
            "/proc/{peer_pid}/ns/", "NoNewPrivs", "Seccomp_filters",
            "CapPrm", "CapEff", "CapBnd", "CapAmb", "TracerPid",
            "parse_mountinfo(", "parse_status(",
        ):
            self.assertIn(item, self.obs)

    def test_observer_never_attests_os_policy(self):
        for claim in (
            '"kernelIsolationAttested": False',
            '"corporateMountsAbsentAttested": False',
            '"seccompFilterSemanticsAttested": False',
            '"inheritedFdIsolationAttested": False',
            '"nativePublicationAuthorized": False',
            '"brokerLaunchAuthorized": False',
            '"writePermissionGranted": False',
            '"releaseReady": False',
        ):
            self.assertIn(claim, self.obs)
        for forbidden in ("subprocess", "os.setuid(", "os.mount(",
                          "os.unshare(", "os.link(", "os.unlink(",
                          "os.chmod(", "os.chown(", "os.replace("):
            self.assertNotIn(forbidden, self.obs)

    def test_systemd_specimen_cannot_start_worker_or_autoinstall(self):
        self.assertIn("RefuseManualStart=yes", self.unit)
        self.assertIn("ExecStart=/usr/bin/false", self.unit)
        self.assertIn("PrivateNetwork=yes", self.unit)
        self.assertIn("PrivateMounts=yes", self.unit)
        self.assertIn("ProtectSystem=strict", self.unit)
        self.assertIn("NoNewPrivileges=yes", self.unit)
        self.assertIn("CapabilityBoundingSet=", self.unit)
        self.assertIn("AmbientCapabilities=", self.unit)
        self.assertIn("RestrictAddressFamilies=AF_UNIX", self.unit)
        self.assertIn("SystemCallFilter=@system-service", self.unit)
        self.assertNotIn("WantedBy=", self.unit)
        self.assertIn("GD31J27N_BLOCKED", self.cmake)
        self.assertIn("native-saveas-kernel-sandbox-observer-source-tests",
                      self.acceptance)


if __name__ == "__main__":
    unittest.main()
