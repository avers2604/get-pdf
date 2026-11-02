"""GD31J27ad source-only contracts: no process launching or production wiring.

The new code only reads already-open, caller-owned read-only FDs. A mock
satisfying these checks is NOT evidence that any Astra Linux VM was tested.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]

class PreopenedMapsElfContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code=(LAB/"native_saveas_proc_maps_elf_collector.py").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")

    def test_fixed_bounded_fd_and_raw_elf64_without_exec(self):
        for token in (
            "def parse_pinned_maps(","def parse_elf64_needed(",
            "def capture_preopened_once(","def _read_bounded_preopened_fd(",
            "MAX_MAPS_BYTES=256*1024","MAX_ELF_BYTES=64*1024*1024",
            "stat.S_ISREG(metadata.st_mode)","fcntl.F_GETFL",
            "os.read(fd,","struct.unpack_from(","approved_disposable_vm is not True",
            "compare_elf_needed(expected_needed,needed)",
            '"kernelMapFactsObserved":True',
            '"elfNeededFactsObserved":True',
            '"mapsFdOriginAttested":False',
            '"osEvidenceAttested":False',
            '"releaseReady":False',
        ):
            with self.subTest(token=token):
                self.assertIn(token,self.code)

    def test_no_command_launch_path_open_or_production_use(self):
        for forbidden in (
            "subprocess.","os.open(","os.close(","os.fork(",
            "os.exec","os.setuid(","os.setgid(","os.chmod(",
            "os.chown(","os.unlink(","os.rename(","os.replace(",
            "os.kill(","socket.socket(","os.unshare(","os.setns(",
            "pidfd_getfd(","systemctl ","readelf ","ldd ","pip install",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.code)
        for runtime in (self.worker,self.broker,self.listener,self.cmake):
            self.assertNotIn("native_saveas_proc_maps_elf_collector",runtime)
        self.assertIn("GD31J27AD_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-proc-maps-elf-source-tests",self.runner)


if __name__=="__main__":
    unittest.main()
