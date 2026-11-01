"""GD31J27k: source-only tests for synthetic O_TMPFILE snapshot ownership.

Deferred until approved offline VM; must NOT claim kernel/c++ behavior passed.
"""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent

class UnlinkedSnapshotContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpp = (LAB / "native_saveas_durable.h").read_text("utf-8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf-8")

    def test_never_hardlink_sdk_owned_writable_inode(self):
        for needle in (
            "O_TMPFILE | O_RDWR | O_CLOEXEC",
            "SnapshotSyntheticBytes(",
            "OpenSnapshotReadOnly(",
            "snapshotWriter.closeNow()",
            "LinkVerifiedSourceFd(snapshotReader.value, output.value",
            "snapshotBefore.st_nlink != 0",
            "snapshotAfter.st_nlink != 1",
            "StableFdDigest(snapshotReader.value, expectedSha)",
        ):
            with self.subTest(needle=needle):
                self.assertIn(needle, self.cpp)
        self.assertNotIn("LinkVerifiedSourceFd(source.value, output.value", self.cpp)
        self.assertNotIn("::linkat(scratch.value, name.constData()", self.cpp)

    def test_owner_uid_private_scratch_and_fd_lifecycle(self):
        for needle in (
            "st_uid != ::geteuid()", "IsOwnerPrivateScratch(",
            "S_ISREG(", "O_NOFOLLOW", "before.st_nlink != 1",
            "snapshotWriter.closeNow()", "Fd(const Fd&) = delete",
            "Fd& operator=(const Fd&) = delete", "::fsync(snapshotWriter.value)",
            "::fchmod(snapshotWriter.value, 0400)",
            "SnapShotFdOwnership", "PROC_SUPER_MAGIC", "::fstatfs(",
        ):
            self.assertIn(needle, self.cpp)
        self.assertIn("Gd31J27G::CreateScratchMarker(", self.policy)
        self.assertIn("Gd31J27G::DurableExclusivePublish(", self.policy)

    def test_no_unsafe_copy_fallback_or_final_cleanup(self):
        self.assertNotIn("::unlink(", self.cpp)
        self.assertNotIn("::rename(", self.cpp)
        self.assertNotIn("::renameat(", self.cpp)
        self.assertNotIn("::renameat2(", self.cpp)
        self.assertNotIn("QFile::copy(", self.cpp)
        self.assertIn("return false;", self.cpp)
        self.assertIn("::fsync(output.value)", self.cpp)
        self.assertIn("SafeBasename(", self.cpp)
        self.assertIn("ExactOwnedMarker(", self.cpp)

if __name__ == "__main__":
    unittest.main()
