"""GD31J27af source-only contract: explicit fixed preopen, never SaveAs."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class DisposableVmPreopenContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code=(LAB/"native_saveas_disposable_vm_preopen.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_fixed_read_only_uid0_pins_and_cleanup(self):
        for required in (
            'EXPLICIT_ACK="OFFLINE_DISPOSABLE_VM_READONLY_DIAGNOSTIC_ONLY"',
            'def preopen_disposable_once(', 'def _open_fixed_proc_map(',
            'def _open_fixed_elf(', 'def _open_fixed_components(',
            'os.O_RDONLY|os.O_CLOEXEC|os.O_NOFOLLOW|os.O_NONBLOCK',
            'pidfd.pin_broker_process(', 'pidfd.pin_worker_process(',
            'origin.review_fd_origins_once(',
            'guards[role].assert_live()',
            'os.geteuid()!=0',
            'os.close(fd)',
            '"fixedReadOnlyDescriptorsOpened":True',
            '"nativePublicationAuthorized":False',
            '"releaseReady":False',
        ):
            with self.subTest(required=required):
                self.assertIn(required,self.code)
        scope=self.code.split("def preopen_disposable_once(",1)[1]
        self.assertLess(scope.index("pidfd.pin_broker_process("),
                        scope.index("_open_fixed_proc_map("))
        self.assertLess(scope.index("_open_fixed_proc_map("),
                        scope.index("origin.review_fd_origins_once("))
        self.assertLess(scope.index("origin.review_fd_origins_once("),
                        scope.index("os.close(fd)"))

    def test_not_executed_from_normal_worker_or_cef(self):
        for app in (self.worker,self.listener,self.broker,self.cmake):
            self.assertNotIn("native_saveas_disposable_vm_preopen",app)
        for forbidden in (
            "subprocess.", "os.setuid(", "os.setgid(", "os.chmod(",
            "os.chown(", "os.fork(", "os.execv(", "os.kill(",
            "os.unlink(", "os.rename(", "os.replace(",
            "os.unshare(", "os.setns(", "socket.socket(",
            "pidfd_getfd(", "open('/home", 'open("/home',
            "systemctl ", "readelf ", "ldd ",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.code)
        self.assertIn("GD31J27AF_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-disposable-vm-preopen-source-tests",
                      self.runner)

if __name__=="__main__":
    unittest.main()
