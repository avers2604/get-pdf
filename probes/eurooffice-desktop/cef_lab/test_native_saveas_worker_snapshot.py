"""GD31J27q: DEFERRED Linux anonymous snapshot regression tests.

Tests only use own tempfile synthetic files. They do not use real OOXML,
EuroOffice SDK, WORM, SMB, business files, systemd or online services.
They must be run ONLY on an approved disposable offline Linux VM. No test
was executed while preparing this Draft PR.
"""
import hashlib
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_fd_worker as worker
import native_saveas_worker_snapshot as snapshot

FIXTURE = "gd31j6_writer.docx"
DESTINATION = "gd31j25_writer_saved.docx"
PAYLOAD = b"GD31J27Q_DISPOSABLE_ONLY_NOT_REAL_OFFICE_DOCUMENT"
SHA = hashlib.sha256(PAYLOAD).hexdigest()


def request(**changes):
    result = {
        "schema": worker.REQUEST_SCHEMA,
        "operation": "inspect_only",
        "fixtureName": FIXTURE,
        "expectedFinalName": DESTINATION,
        "sessionId": "102030405060708090a0b0c0d0e0f010",
        "tabId": "f010e0d0c0b0a0908070605040302010",
        "sourceSha256": SHA,
        "laboratoryOnly": True,
    }
    result.update(changes)
    return result


@unittest.skipUnless(sys.platform.startswith("linux") and os.geteuid() != 0,
                     "Linux non-root O_TMPFILE/procfs regression requires disposable VM")
class AnonymousSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="getdoc-gd31j27q-")
        root = Path(self.tmp.name)
        self.scratch = root / "gd31j26-private-ABCDEF"
        self.output = root / "gd31j25-output"
        self.scratch.mkdir(mode=0o700)
        self.output.mkdir(mode=0o700)
        self.source = self.scratch / FIXTURE
        self.source.write_bytes(PAYLOAD)
        (self.scratch / worker.PRIVATE_MARKER).write_bytes(worker.PRIVATE_BYTES)
        (self.output / worker.OUTPUT_MARKER).write_bytes(worker.OUTPUT_BYTES)
        flags = os.O_RDONLY | os.O_CLOEXEC
        self.handles = (
            os.open(self.source, flags | os.O_NOFOLLOW),
            os.open(self.scratch, flags | os.O_DIRECTORY),
            os.open(self.output, flags | os.O_DIRECTORY),
        )

    def tearDown(self):
        for fd in self.handles:
            os.close(fd)
        self.tmp.cleanup()

    def test_default_entrypoint_refuses_all_publication_without_marker(self):
        with self.assertRaises(snapshot.SnapshotError):
            snapshot.publish_disposable_fixture(self.handles, request(),
                                                worker_uid=os.geteuid())
        self.assertFalse((self.output / DESTINATION).exists())

    def test_anonymous_snapshot_is_independent_of_writable_sdk_source(self):
        published = snapshot.publish_disposable_fixture(
            self.handles, request(), worker_uid=os.geteuid(),
            operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        self.assertEqual("synthetic-link-created-not-approved", published["state"])
        self.assertEqual(SHA, published["sha256"])
        self.assertTrue((self.output / DESTINATION).exists())
        final = self.output / DESTINATION
        self.assertEqual(PAYLOAD, final.read_bytes())
        self.assertNotEqual(os.stat(self.source).st_ino, os.stat(final).st_ino)
        self.assertEqual(1, os.stat(final).st_nlink)
        self.assertEqual(0o400, os.stat(final).st_mode & 0o777)
        self.source.write_bytes(b"SDK_WRITER_CHANGED_THE_NAMED_SOURCE")
        self.assertEqual(PAYLOAD, final.read_bytes())
        self.assertFalse(published["releaseReady"])
        self.assertFalse(published["approvedForProduction"])

    def test_eexist_is_never_overwritten_and_has_no_rollback(self):
        final = self.output / DESTINATION
        final.write_bytes(b"KEEP_EXISTING_SYNTHETIC_FILE")
        with self.assertRaises(snapshot.SnapshotError):
            snapshot.publish_disposable_fixture(
                self.handles, request(), worker_uid=os.geteuid(),
                operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        self.assertEqual(b"KEEP_EXISTING_SYNTHETIC_FILE", final.read_bytes())

    def test_directory_fsync_error_keeps_final_for_manual_review(self):
        from unittest import mock
        final = self.output / DESTINATION
        original_fsync = os.fsync
        output_fd = self.handles[2]
        def fail_only_final_directory(fd):
            if fd == output_fd:
                raise OSError(5, "synthetic directory fsync fault")
            return original_fsync(fd)
        with mock.patch.object(snapshot.os, "fsync",
                               side_effect=fail_only_final_directory):
            with self.assertRaises(snapshot.SnapshotError):
                snapshot.publish_disposable_fixture(
                    self.handles, request(), worker_uid=os.geteuid(),
                    operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        # Success is uncertain. Never automatically unlink an already visible
        # file, including if its directory fsync syscall has failed.
        self.assertTrue(final.exists())
        self.assertEqual(PAYLOAD, final.read_bytes())

    def test_source_name_swapped_to_symlink_does_not_publish(self):
        old = self.scratch / "safe-synthetic-original"
        self.source.rename(old)
        self.source.symlink_to(old)
        with self.assertRaises(snapshot.SnapshotError):
            snapshot.publish_disposable_fixture(
                self.handles, request(), worker_uid=os.geteuid(),
                operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        self.assertFalse((self.output / DESTINATION).exists())

    def test_missing_marker_or_wrong_sha_never_creates_final(self):
        for bad in (request(sourceSha256="b" * 64),
                    request(expectedFinalName="customer.docx")):
            with self.subTest(bad=bad):
                with self.assertRaises((snapshot.SnapshotError, worker.WorkerError)):
                    snapshot.publish_disposable_fixture(
                        self.handles, bad, worker_uid=os.geteuid(),
                        operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        self.assertFalse((self.output / DESTINATION).exists())
        (self.output / worker.OUTPUT_MARKER).unlink()
        with self.assertRaises((snapshot.SnapshotError, worker.WorkerError)):
            snapshot.publish_disposable_fixture(
                self.handles, request(), worker_uid=os.geteuid(),
                operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        self.assertFalse((self.output / DESTINATION).exists())

    def test_unsupported_tmpfile_never_falls_back_to_named_or_original_link(self):
        original = snapshot.os.open
        def fail_anonymous(path, flags, *args, **kwargs):
            if flags & os.O_TMPFILE == os.O_TMPFILE:
                raise OSError(95, "synthetic O_TMPFILE unsupported")
            return original(path, flags, *args, **kwargs)
        from unittest import mock
        with mock.patch.object(snapshot.os, "open", side_effect=fail_anonymous):
            with self.assertRaises(snapshot.SnapshotError):
                snapshot.publish_disposable_fixture(
                    self.handles, request(), worker_uid=os.geteuid(),
                    operator_vm_gate=snapshot.VM_TEST_ONLY_EXPLICIT_GATE)
        self.assertFalse((self.output / DESTINATION).exists())


if __name__ == "__main__":
    unittest.main()
