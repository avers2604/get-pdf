"""GD31J27p static source boundary for the real post-UID broker endpoint."""
from pathlib import Path
import unittest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


class BrokerListenerSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.broker = (HERE/"native_saveas_broker_listener.py").read_text("utf8")
        cls.dialer = (HERE/"native_saveas_worker_dialer.py").read_text("utf8")
        cls.worker = (HERE/"native_saveas_fd_worker.py").read_text("utf8")
        cls.service = (HERE/"getdoc-gd31j27p-broker.blocked.service").read_text("utf8")
        cls.worker_service = (HERE/"getdoc-gd31j27p-worker.blocked.service").read_text("utf8")
        cls.cmake=(HERE/"CMakeLists.txt").read_text("utf8")
        cls.plan=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_broker_binds_only_after_os_uid_selection(self):
        for token in (
            "validate_broker_identity(", "os.geteuid()",
            "os.getuid() != os.geteuid()",
            "os.lstat(dialer.FIXED_SOCKET_DIR)",
            "_absent_final_socket()", "socket.AF_UNIX",
            "socket.SOCK_SEQPACKET", "socket.SO_PEERCRED",
            "listener.bind(dialer.FIXED_SOCKET)", "listener.listen(1)",
            "listener.accept()", "validate_worker_peer(",
            "channel.sendall(", "return 2",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.broker)
        for banned in (
            "subprocess", "os.setuid(", "os.setgid(", "os.unlink(",
            "os.remove(", "os.replace(", "os.rename(",
            "os.link(", "os.open(", "os.fork(", "os.execv(",
            "socket.socketpair(",
        ):
            self.assertNotIn(banned, self.broker)
        self.assertIn('channel.recv(512)', self.broker)
        self.assertNotIn("channel.sendmsg(", self.broker)

    def test_units_are_noninstallable_and_denial_only(self):
        for unit in (self.service,self.worker_service):
            self.assertIn("RefuseManualStart=yes", unit)
            self.assertIn("ConditionPathExists=", unit)
            self.assertIn("User=getdoc-lab-saveas-", unit)
            self.assertIn("Group=getdoc-lab-ipc", unit)
            self.assertIn("NoNewPrivileges=yes", unit)
            self.assertIn("PrivateNetwork=yes", unit)
            self.assertIn("PrivateMounts=yes", unit)
            self.assertIn("ProtectSystem=strict", unit)
            self.assertIn("CapabilityBoundingSet=", unit)
            self.assertNotIn("WantedBy=",unit)
        self.assertIn("ExecStart=/usr/bin/python3 -B /opt/getdoc-lab/cef_lab/native_saveas_broker_listener.py",self.service)
        self.assertIn("GD31J27P_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-post-uid-broker-source-tests",self.plan)

    def test_worker_remains_no_scm_rights_publish(self):
        self.assertIn('socket.MSG_CMSG_CLOEXEC',self.worker)
        self.assertIn('"writePermissionGranted": False',self.worker)
        self.assertIn("native-saveas-post-uid-broker-tests",self.plan)


if __name__=="__main__":
    unittest.main()
