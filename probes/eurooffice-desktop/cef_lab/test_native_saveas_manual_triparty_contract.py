"""GD31J27ah source-only safety contracts, NOT real OS or IPC tests.

The separate manual entrypoints must never be imported by production,
registered as systemd services, autostarted or treated as SaveAs authority.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class ManualTripartySourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code=(LAB/"native_saveas_manual_triparty_devnull.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.root=(LAB/"native_saveas_root_supervisor.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_real_socket_transport_only_three_null_fds_and_distinct_uid(self):
        for required in (
            'BASE="/run/getdoc-gd31j27ah-disposable"',
            'BROKER_UID=44001', 'WORKER_UID=44002','IPC_GID=44003',
            'VM_ENV_VALUE="OFFLINE_DISPOSABLE_NO_DOCS"',
            'MANUAL_ACK="I_APPROVE_DISPOSABLE_VM_NO_DOCUMENTS_DENIAL_ONLY"',
            'REQUEST=b"GETDOC_GD31J27AH_THREE_DEVNULL_FDS_INSPECT_ONLY',
            'os.open("/dev/null",flags)',
            'socket.SOCK_SEQPACKET|socket.SOCK_CLOEXEC',
            'socket.SO_PEERCRED',
            'socket.SCM_RIGHTS',
            'socket.MSG_TRUNC|socket.MSG_CTRUNC',
            'stat.S_ISCHR(data.st_mode)',
            'data.st_rdev!=reference.st_rdev',
            'os.O_NOFOLLOW',
            'def _worker_once():', 'def _broker_once():',
            'def _supervisor_once():',
            'def _require_denial(',
            'return 2  # ALWAYS DENIED',
            '"nativePublicationAuthorized":False',
            '"releaseReady":False',
        ):
            with self.subTest(required=required):
                self.assertIn(required,self.code)
        self.assertIn("GD31J27AH_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-manual-triparty-source-tests",self.acceptance)

    def test_no_process_launch_privileges_service_or_corporate_paths(self):
        for content in (self.worker,self.broker,self.listener,self.root,self.cmake):
            self.assertNotIn("native_saveas_manual_triparty_devnull",content)
        for forbidden in (
            "subprocess", "os.fork(", "os.exec", "os.setuid(",
            "os.setgid(", "os.chown(", "os.chmod(", "os.unlink(",
            "os.rename(", "os.replace(", "os.kill(",
            "systemctl", "sudo ", "runuser ", "os.open('/home",
            "os.open('/mnt", "os.open('/srv", "os.open('/media",
            "socket.AF_INET", "socket.AF_INET6", "http://", "https://",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.code)
        self.assertIn('if __name__=="__main__":',self.code)
        self.assertNotIn("WantedBy=",self.code)

if __name__=="__main__":
    unittest.main()
