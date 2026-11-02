"""GD31J27ac deferred static contracts for memory-only offline VM evidence.

Source checks are not cryptographic attestation, functional testing,
independent signature trust, validated Astra VM or production permission.
"""
from pathlib import Path
import unittest

LAB=Path(__file__).resolve().parent
ROOT=LAB.parents[2]


class OfflineVmEvidenceSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source=(LAB/"native_saveas_offline_vm_evidence.py").read_text("utf8")
        cls.runner=(ROOT/"scripts/office_acceptance.py").read_text("utf8")
        cls.cmake=(LAB/"CMakeLists.txt").read_text("utf8")
        cls.worker=(LAB/"native_saveas_fd_worker.py").read_text("utf8")
        cls.listener=(LAB/"native_saveas_broker_listener.py").read_text("utf8")
        cls.broker=(LAB/"native_saveas_fd_broker.py").read_text("utf8")

    def test_no_imports_execution_install_or_unsafe_filesystem_ops(self):
        for forbidden in (
            "subprocess.", "os.system(", "os.open(", "os.readlink(",
            "os.setuid(", "os.setgid(", "os.chmod(", "os.chown(",
            "os.unlink(", "os.rename(", "os.replace(", "os.fork(",
            "os.kill(", "socket.socket(", "pip install", "systemctl ",
            "pidfd_getfd(", "os.unshare(", "os.setns(",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden,self.source)
        for runtime in (self.worker,self.listener,self.broker,self.cmake):
            self.assertNotIn("native_saveas_offline_vm_evidence",runtime)

    def test_wheel_record_strict_and_untrusted_os_evidence_remain_denied(self):
        for key in (
            "def inspect_wheel_record(",
            "zipfile.ZipFile(io.BytesIO(wheel_bytes)",
            "MAX_UNCOMPRESSED_BYTES",
            "MAX_ZIP_ENTRIES",
            "stat.S_IFMT(item.external_attr>>16)",
            "file_bytes[name]",
            "csv.reader(",
            "hmac.compare_digest(",
            "def inspect_external_snapshot(",
            "sbom.inspect_effective_unit_snapshot(",
            "def review_offline_vm_evidence(",
            "sbom.inspect_signed_catalog(",
            "expected_processes=expected_processes",
            '"osEvidenceAttested":False',
            '"dependencyClosureAttested":False',
            '"effectiveUnitAttested":False',
            '"nativePublicationAuthorized":False',
            '"releaseReady":False',
        ):
            with self.subTest(key=key):
                self.assertIn(key,self.source)
        self.assertIn("GD31J27AC_BLOCKED:",self.cmake)
        self.assertIn("native-saveas-offline-vm-evidence-tests",self.runner)
        self.assertIn("native-saveas-offline-vm-source-tests",self.runner)


if __name__=="__main__":
    unittest.main()
