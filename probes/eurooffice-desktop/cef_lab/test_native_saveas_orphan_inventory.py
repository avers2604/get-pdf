"""GD31J27g: read-only crash leftovers inventory tests, deferred execution."""
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_orphan_inventory as inventory

MARKER = b"GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n"
PRIVATE = b"GETDOC_GD31J27G_PRIVATE_SCRATCH_ONLY\n"


class OrphanInventoryTests(unittest.TestCase):
    def prepare(self, root):
        stage = Path(root)
        output = stage / "gd31j25-output"
        output.mkdir(mode=0o700)
        (output / "gd31j25-output-lab.marker").write_bytes(MARKER)
        private = output / "gd31j26-private-ABCDEF"
        private.mkdir(mode=0o700)
        (private / "gd31j27g-private-owner.marker").write_bytes(PRIVATE)
        (private / "gd31j6_writer.docx").write_bytes(b"fake-private-shadow")
        (private / "gd31j25_writer_saved.docx").write_bytes(b"fake-native-output")
        return output, private

    def age(self, private, secs=90000):
        timestamp = time.time() - secs
        for name in ("gd31j27g-private-owner.marker", "gd31j6_writer.docx",
                     "gd31j25_writer_saved.docx"):
            os.utime(private / name, (timestamp, timestamp))
        os.utime(private, (timestamp, timestamp))

    def test_only_old_strictly_owned_synthetic_leftovers_are_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, private = self.prepare(tmp)
            self.age(private)
            result = inventory.scan(Path(tmp), now=time.time())
            self.assertEqual(1, result["eligibleForManualReview"])
            self.assertEqual(0, result["rejected"])
            self.assertEqual(["gd31j26-private-ABCDEF"], result["scratchNames"])
            self.assertFalse(result["safeToDeleteAutomatically"])
            self.assertTrue(private.exists())
            self.assertTrue((private / "gd31j6_writer.docx").exists())

    def test_recent_scratch_is_not_classified_as_old(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.prepare(tmp)
            result = inventory.scan(Path(tmp), now=time.time())
            self.assertEqual(0, result["eligibleForManualReview"])
            self.assertEqual(1, result["rejected"])

    def test_user_file_or_symlink_or_bad_owner_marker_refused(self):
        for mutation in ("unexpected", "symlink", "marker", "name", "group_write"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as tmp:
                _, private = self.prepare(tmp)
                if mutation == "unexpected":
                    (private / "Customer-Contract.docx").write_text("NEVER DELETE", "utf8")
                elif mutation == "symlink":
                    (private / "foreign-link").symlink_to("/etc/passwd")
                elif mutation == "marker":
                    (private / "gd31j27g-private-owner.marker").write_bytes(b"forged")
                elif mutation == "name":
                    private.rename(private.parent / "user-folder")
                    private = private.parent / "user-folder"
                elif mutation == "group_write":
                    private.chmod(0o770)
                self.age(private)
                result = inventory.scan(Path(tmp), now=time.time())
                self.assertEqual(0, result["eligibleForManualReview"])
                self.assertFalse(result["safeToDeleteAutomatically"])
                self.assertTrue(private.exists())

    def test_final_published_output_is_never_considered_an_orphan(self):
        with tempfile.TemporaryDirectory() as tmp:
            output, private = self.prepare(tmp)
            (output / "gd31j25_writer_saved.docx").write_bytes(b"saved")
            self.age(private)
            result = inventory.scan(Path(tmp), now=time.time())
            self.assertEqual(1, result["eligibleForManualReview"])
            self.assertTrue((output / "gd31j25_writer_saved.docx").exists())


if __name__ == "__main__":
    unittest.main()
