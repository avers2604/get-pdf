"""GD31J27q source contracts — lab-only anonymous publication is NOT IPC.

Deferred. Static source assertions do not prove working O_TMPFILE, fsync,
kernel procfs or actual EuroOffice integration. Those require offline VM.
"""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent
ROOT = LAB.parents[2]


class WorkerSnapshotSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.snapshot = (LAB / "native_saveas_worker_snapshot.py").read_text("utf8")
        cls.worker = (LAB / "native_saveas_fd_worker.py").read_text("utf8")
        cls.broker = (LAB / "native_saveas_broker_listener.py").read_text("utf8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf8")
        cls.cmake = (LAB / "CMakeLists.txt").read_text("utf8")
        cls.plan = (ROOT / "scripts/office_acceptance.py").read_text("utf8")

    def test_never_publish_original_sdk_fd_or_named_fallback(self):
        for token in (
            "os.O_TMPFILE", "os.O_RDWR", "os.O_CLOEXEC",
            "os.fsync(writer)", "os.fchmod(writer, 0o400)",
            "os.close(writer)", "writer = -1",
            "reader = os.open(str(writer), os.O_RDONLY",
            "ctypes.CDLL(None, use_errno=True)", "libc.fstatfs",
            "PROC_SUPER_MAGIC", 'os.link(str(read_fd), final_name',
            "src_dir_fd=procfd", "dst_dir_fd=output_fd",
            "follow_symlinks=True", "os.fsync(output_fd)",
            "source.st_uid != worker_uid",
            "worker.inspect_fds(handles, parsed",
        ):
            with self.subTest(token=token):
                self.assertIn(token, self.snapshot)
        self.assertNotIn("os.link(str(source_fd)", self.snapshot)
        self.assertNotIn("os.link(parsed[", self.snapshot)
        self.assertNotIn("os.rename(", self.snapshot)
        self.assertNotIn("os.replace(", self.snapshot)
        self.assertNotIn("os.unlink(", self.snapshot)
        self.assertNotIn("shutil.copy(", self.snapshot)

    def test_not_reachable_from_renderer_broker_or_worker(self):
        self.assertIn("def publish_disposable_fixture(", self.snapshot)
        self.assertIn("operator_vm_gate != VM_TEST_ONLY_EXPLICIT_GATE", self.snapshot)
        self.assertNotIn("native_saveas_worker_snapshot", self.worker)
        self.assertNotIn("native_saveas_worker_snapshot", self.broker)
        self.assertNotIn("native_saveas_worker_snapshot", self.policy)
        self.assertNotIn("native_saveas_worker_snapshot", self.cmake)
        self.assertNotIn("publish_disposable_fixture(", self.worker)
        self.assertNotIn("publish_disposable_fixture(", self.broker)

    def test_failure_never_rolls_back_link_or_approves_business_saves(self):
        self.assertIn('linked = True', self.snapshot)
        self.assertIn("never unlink after link", self.snapshot.lower())
        for phrase in (
            '"nativePublicationAuthorized": False',
            '"nativeSaveProven": False',
            '"documentVerified": False',
            '"saveVerified": False',
            '"approvedForProduction": False',
            '"releaseReady": False',
            '"writePermissionGranted": False',
        ):
            self.assertIn(phrase, self.snapshot)
        self.assertIn("GD31J27Q_BLOCKED:", self.cmake)
        self.assertIn("native-saveas-worker-snapshot-source-tests", self.plan)


if __name__ == "__main__":
    unittest.main()
