"""GD31J27j disposable-filesystem Linux regression, deferred execution.

Python stdlib exercises the same Linux /proc/self/fd linkat semantics used as
the unprivileged, verified-procfs fallback. Does NOT compile or attest C++.
Only new synthetic fixtures in TemporaryDirectory; never corporate files.
"""
import os
from pathlib import Path
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform.startswith("linux") and
                     Path("/proc/self/fd").is_dir(),
                     "Requires an approved Linux VM with procfs")
class DescriptorBoundSyntheticLinkTests(unittest.TestCase):
    def test_name_swap_cannot_turn_published_fd_into_attacker_file(self):
        with tempfile.TemporaryDirectory(prefix="getdoc-gd31j27j-") as tmp:
            root = Path(tmp)
            output = root / "output"
            output.mkdir()
            old = root / "gd31j25_writer_saved.docx"
            old.write_bytes(b"GD31J27J_VERIFIED_SYNTHETIC")
            fd = os.open(old, os.O_RDONLY | os.O_NOFOLLOW)
            try:
                # The directory entry is changed AFTER a trusted fd is held.
                old.rename(root / "verified-now-renamed")
                old.write_bytes(b"GD31J27J_UNTRUSTED_SUBSTITUTION")
                proc = os.open("/proc/self/fd", os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.link(str(fd), "gd31j25_writer_saved.docx",
                            src_dir_fd=proc, dst_dir_fd=os.open(
                                output, os.O_RDONLY | os.O_DIRECTORY),
                            follow_symlinks=True)
                finally:
                    os.close(proc)
                final = output / "gd31j25_writer_saved.docx"
                self.assertEqual(b"GD31J27J_VERIFIED_SYNTHETIC", final.read_bytes())
                self.assertNotEqual(old.stat().st_ino, final.stat().st_ino)
                with self.assertRaises(FileExistsError):
                    os.link(str(fd), str(final), src_dir_fd=os.open(
                        "/proc/self/fd", os.O_RDONLY | os.O_DIRECTORY),
                        follow_symlinks=True)
            finally:
                os.close(fd)

    def test_normal_final_name_is_never_overwritten(self):
        with tempfile.TemporaryDirectory(prefix="getdoc-gd31j27j-") as tmp:
            root = Path(tmp)
            origin = root / "synthetic.docx"
            dest = root / "gd31j25_writer_saved.docx"
            origin.write_bytes(b"SAFE")
            dest.write_bytes(b"PRESERVE")
            fd = os.open(origin, os.O_RDONLY | os.O_NOFOLLOW)
            proc = os.open("/proc/self/fd", os.O_RDONLY | os.O_DIRECTORY)
            try:
                with self.assertRaises(FileExistsError):
                    os.link(str(fd), dest.name, src_dir_fd=proc,
                            dst_dir_fd=os.open(root, os.O_RDONLY | os.O_DIRECTORY),
                            follow_symlinks=True)
                self.assertEqual(b"PRESERVE", dest.read_bytes())
            finally:
                os.close(fd)
                os.close(proc)


if __name__ == "__main__":
    unittest.main()
