"""GD31J27t deferred static security contracts, never root service testing."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class RootSupervisorSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.s=(LAB/"native_saveas_root_supervisor.py").read_text("utf8")
        cls.unit=(LAB/"getdoc-gd31j27t-root-supervisor.blocked.service").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.bridge=(LAB/"native_saveas_three_party_bridge.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_root_only_fixed_paths_and_verified_preopened_sources(self):
        for item in (
            'FIXED_MANIFEST = "/etc/getdoc-lab/gd31j27t-root-supervisor.json"',
            'FIXED_BASE = "/run/getdoc-gd31j27t-disposable"',
            'SCRATCH = "gd31j26-private-ABCDEF"',
            'OUTPUT = "gd31j25-output"',
            'os.O_NOFOLLOW', 'os.O_CLOEXEC', 'dir_fd=base',
            'dir_fd=scratch', 'worker.inspect_fds(roles, packet',
            'os.getuid() != 0', 'os.geteuid() != 0',
            'socket.SO_PEERCRED', 'socket.SOCK_SEQPACKET',
            'socket.SCM_RIGHTS', 'sock.sendmsg(',
            'rights.tobytes()', 'if size != len(packet)',
            'self.consumed = True', '"nativePublicationAuthorized": False',
            '"trustedSupervisorAttested": False',
            '"releaseReady": False',
        ):
            with self.subTest(item=item):
                self.assertIn(item,self.s)

    def test_sender_not_called_by_installed_paths_or_worker(self):
        for content in (self.worker,self.broker,self.bridge,self.cmake):
            self.assertNotIn("native_saveas_root_supervisor",content)
        for forbidden in (
            "subprocess", "os.setuid(", "os.setgid(", "os.chown(",
            "os.chmod(", "os.unlink(", "os.rename(", "os.replace(",
            "os.mkdir(", "os.makedirs(", "os.link(", "os.execv(",
            "os.fork(", "socket.socketpair(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.s)

    def test_root_service_cannot_start_and_build_always_blocks(self):
        self.assertIn("RefuseManualStart=yes",self.unit)
        self.assertIn("ExecStart=/usr/bin/false",self.unit)
        self.assertIn("PrivateNetwork=yes",self.unit)
        self.assertIn("PrivateMounts=yes",self.unit)
        self.assertIn("ProtectSystem=strict",self.unit)
        self.assertIn("CapabilityBoundingSet=",self.unit)
        self.assertNotIn("WantedBy=",self.unit)
        self.assertIn("GD31J27T_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-root-supervisor-source-tests",self.acceptance)

if __name__=="__main__":
    unittest.main()
