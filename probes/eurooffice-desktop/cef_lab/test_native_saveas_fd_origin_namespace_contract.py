"""GD31J27ae deferred static source-only contracts; no Astra VM execution."""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]


class FdOriginContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.origin=(LAB/"native_saveas_fd_origin_namespace.py").read_text("utf8")
        cls.parser=(LAB/"native_saveas_proc_maps_elf_collector.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.acceptance=(ROOT/"scripts/office_acceptance.py").read_text("utf8")

    def test_fd_source_pidfd_kernel_namespace_digest_before_after(self):
        for piece in (
            "def review_fd_origins_once(",
            "def _valid_inputs(",
            "pidfd.pin_broker_process(",
            "pidfd.pin_worker_process(",
            'os.readlink(f"/proc/self/fd/{fd}")',
            'os.stat(f"/proc/{pid}/ns/{ns}")',
            "os.pread(fd,",
            "fcntl.F_GETFL",
            "stat.S_ISREG(before.st_mode)",
            "hmac.compare_digest(",
            "maps.capture_preopened_once(",
            "guards[role].assert_live()",
            "_kernel_fd_targets(proc_map_fds,elf_fds,expected_pids)",
            "_namespace_snapshot(expected_pids,expected_namespaces)",
            '"fdOriginAttested":False',
            '"osEvidenceAttested":False',
            '"releaseReady":False',
        ):
            with self.subTest(piece=piece):
                self.assertIn(piece,self.origin)
        body=self.origin.split("def review_fd_origins_once(",1)[1]
        self.assertLess(body.index("pidfd.pin_broker_process("),
                        body.index("maps.capture_preopened_once("))
        self.assertLess(body.index("maps.capture_preopened_once("),
                        body.rindex("_namespace_snapshot("))
        self.assertIn("previous_end<=start<end",self.parser)

    def test_no_production_integration_or_privilege_promotion(self):
        for source in (self.worker,self.listener,self.broker,self.cmake):
            self.assertNotIn("native_saveas_fd_origin_namespace",source)
        for forbidden in (
            "subprocess.", "os.open(", "os.close(", "os.fork(",
            "os.setuid(", "os.setgid(", "os.chown(", "os.chmod(",
            "os.rename(", "os.unlink(", "os.replace(",
            "os.kill(", "socket.socket(", "os.unshare(", "os.setns(",
            "pidfd_getfd(", "systemctl ", "readelf ", "ldd ",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.origin)
        self.assertIn("GD31J27AE_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-fd-origin-namespace-source-tests",
                      self.acceptance)


if __name__=="__main__":
    unittest.main()
