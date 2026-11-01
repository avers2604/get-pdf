"""GD31J27g source-only durability/fail-closed contract, deferred execution."""
from pathlib import Path
import unittest

HERE = Path(__file__).resolve().parent


class DurableSyntheticPolicyContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.policy = (HERE / "native_saveas_policy.h").read_text(encoding="utf-8")
        cls.durable = (HERE / "native_saveas_durable.h").read_text(encoding="utf-8")
        cls.inventory = (HERE / "native_saveas_orphan_inventory.py").read_text(encoding="utf-8")

    def test_publication_uses_dirfd_no_replace_and_durable_barriers(self):
        for token in (
            "::open(", "O_DIRECTORY", "O_NOFOLLOW", "O_CLOEXEC",
            "::openat(", "::fstat(", "::fstatat(",
            "::linkat(", "AT_SYMLINK_NOFOLLOW",
            "::fsync(", "fsync_output", "fsync_directory",
            "GD31J27G_PRIVATE_SCRATCH_ONLY",
        ):
            self.assertIn(token, self.durable)
        self.assertIn("Gd31J27G::CreateScratchMarker(", self.policy)
        self.assertIn("Gd31J27G::DurableExclusivePublish(", self.policy)
        self.assertIn("Gd31J27G::SyncScratchParent(", self.policy)
        self.assertNotIn("::link(from.constData(), to.constData())", self.policy)
        self.assertNotIn("::unlink(to.constData())", self.policy)
        self.assertIn("publishedSha = digest;", self.policy)

    def test_durable_failure_never_unlinks_final_output(self):
        self.assertNotIn("::unlink(", self.durable)
        self.assertNotIn("::remove(", self.durable)
        self.assertNotIn("QFile::remove(", self.policy)
        self.assertIn("return false;", self.durable)
        self.assertIn("unknown durability", self.durable.lower())
        self.assertIn("if (!Gd31J27G::DurableExclusivePublish(", self.policy)

    def test_orphan_inventory_never_modifies_files(self):
        for forbidden in ("os.unlink(", "os.remove(", "os.rmdir(", "shutil.rmtree(",
                          ".unlink(", ".rmdir(", "subprocess", "open(path, \"w"):
            self.assertNotIn(forbidden, self.inventory)
        self.assertIn('"safeToDeleteAutomatically": False', self.inventory)
        self.assertIn("O_NOFOLLOW", self.inventory)
        self.assertIn("MAX_AGE", self.inventory)


if __name__ == "__main__":
    unittest.main()
