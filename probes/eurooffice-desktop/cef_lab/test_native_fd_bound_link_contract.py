"""GD31J27j: deferred SOURCE contract for descriptor-bound synthetic publication.

Do not accept source filename linkat, replace/rename fallback, unvalidated
procfs or post-link automatic unlink. No Qt, CEF, SDK, tests executed yet.
"""
from pathlib import Path
import unittest

LAB = Path(__file__).resolve().parent


class NativeFdBoundLinkSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = (LAB / "native_saveas_durable.h").read_text("utf8")
        cls.policy = (LAB / "native_saveas_policy.h").read_text("utf8")

    def test_publication_hardlinks_verified_fd_not_mutable_source_name(self):
        for phrase in (
            "LinkVerifiedSourceFd(", "AT_EMPTY_PATH", "AT_SYMLINK_FOLLOW",
            "PROC_SUPER_MAGIC", "::fstatfs(", "F_DUPFD_CLOEXEC",
            "::linkat(sourceFd, \"\", outputFd, basename, AT_EMPTY_PATH)",
            "::linkat(procFdDirectory", "std::to_string(sourceFd)",
            "SameFile(snapshotAfter, published)",
        ):
            with self.subTest(phrase=phrase):
                self.assertIn(phrase, self.code)
        self.assertNotIn(
            "linkat(scratch.value, name.constData(),\n"
            "                 output.value, name.constData(), 0)",
            self.code)
        self.assertNotIn("::renameat(", self.code)
        self.assertNotIn("::renameat2(", self.code)
        self.assertNotIn("::unlink(", self.code)

    def test_validation_is_against_owned_fd_and_existing_exact_synthetic_scope(self):
        for phrase in (
            "StableFdDigest(source.value, expectedSha)",
            "fsync_output(source.value)",
            "SameFile(before, latest)", "SameFile(latest, named)",
            "ExactOwnedMarker(scratch.value", "ExactOwnedMarker(output.value",
            "O_NOFOLLOW", "S_ISREG(", "before.st_nlink != 1",
            "fsync_directory(", "::fsync(output.value)",
            "LinkVerifiedSourceFd(snapshotReader.value, output.value",
        ):
            with self.subTest(phrase=phrase):
                self.assertIn(phrase, self.code)
        self.assertIn("Gd31J27G::DurableExclusivePublish(", self.policy)
        self.assertIn("publishedSha = digest;", self.policy)

    def test_dir_fds_and_procfs_cannot_be_pathname_fallback(self):
        self.assertIn('::open("/proc/self/fd",', self.code)
        self.assertIn("O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC", self.code)
        self.assertIn("procfs.f_type != PROC_SUPER_MAGIC", self.code)
        self.assertIn("return false;", self.code)
        self.assertNotIn("QFile::copy(", self.code)
        self.assertNotIn("::link(from.constData(), to.constData())", self.code)


if __name__ == "__main__":
    unittest.main()
