"""GD31J27k: Linux O_TMPFILE synthetic copy-on-publication regression.

Deferred: requires approved disposable offline Linux VM/filesystem with O_TMPFILE.
No SDK, real users or corporate files are accessed. An unsupported O_TMPFILE is
a FAILED acceptance test, not success-by-skip on Linux.
"""
import os
from pathlib import Path
import sys
import tempfile
import unittest

@unittest.skipUnless(sys.platform.startswith("linux"),
                     "Linux-specific anonymous-fd fixture")
class LinuxUnlinkedSnapshotTests(unittest.TestCase):
    def test_sdk_source_mutation_after_snapshot_does_not_change_final(self):
        with tempfile.TemporaryDirectory(prefix="getdoc-gd31j27k-") as tmp:
            root = Path(tmp)
            private = root / "gd31j26-private-ABCDEF"
            output = root / "gd31j25-output"
            private.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            original = private / "gd31j25_writer_saved.docx"
            original.write_bytes(b"VERIFIED_LAB_DOCUMENT")
            private_fd = os.open(private, os.O_RDONLY | os.O_DIRECTORY)
            output_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY)
            proc_fd = os.open("/proc/self/fd", os.O_RDONLY | os.O_DIRECTORY)
            writer = reader = -1
            try:
                self.assertTrue(hasattr(os, "O_TMPFILE"))
                writer = os.open(".", os.O_TMPFILE | os.O_RDWR | os.O_CLOEXEC,
                                 0o600, dir_fd=private_fd)
                self.assertEqual(0, os.fstat(writer).st_nlink)
                os.write(writer, original.read_bytes())
                os.fsync(writer)
                os.fchmod(writer, 0o400)
                os.fsync(writer)
                reader = os.open(str(writer), os.O_RDONLY | os.O_CLOEXEC,
                                 dir_fd=proc_fd)
                self.assertEqual(os.fstat(writer).st_ino,
                                 os.fstat(reader).st_ino)
                os.close(writer)
                writer = -1
                os.link(str(reader), original.name, src_dir_fd=proc_fd,
                        dst_dir_fd=output_fd, follow_symlinks=True)
                saved = output / original.name
                self.assertEqual(1, saved.stat().st_nlink)
                self.assertNotEqual(original.stat().st_ino, saved.stat().st_ino)
                original.write_bytes(b"MODIFIED_BY_SIMULATED_SDK_WRITER")
                self.assertEqual(b"VERIFIED_LAB_DOCUMENT", saved.read_bytes())
                self.assertEqual(0o400, saved.stat().st_mode & 0o777)
                with self.assertRaises(FileExistsError):
                    os.link(str(reader), original.name, src_dir_fd=proc_fd,
                            dst_dir_fd=output_fd, follow_symlinks=True)
            finally:
                if writer >= 0:
                    os.close(writer)
                if reader >= 0:
                    os.close(reader)
                os.close(proc_fd)
                os.close(output_fd)
                os.close(private_fd)

    def test_closed_fd_cannot_be_reused_for_publication(self):
        with tempfile.TemporaryDirectory(prefix="getdoc-gd31j27k-") as tmp:
            root = Path(tmp)
            os.chmod(root, 0o700)
            source = root / "synthetic.docx"
            source.write_bytes(b"SNAPSHOT")
            fd = os.open(source, os.O_RDONLY | os.O_CLOEXEC)
            os.close(fd)
            # A closed handle is never a transferable capability. A numerical
            # fd can be reused by OS; the production code forbids stale fd use
            # with scope-owned RAII and immediate same-inode checks.
            with self.assertRaises(OSError):
                os.fstat(fd)

if __name__ == "__main__":
    unittest.main()
