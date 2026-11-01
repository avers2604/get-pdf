"""GD31J27l: offline-only FD worker protocol contracts; DO NOT start worker.

No CEF/SDK, no file publication or privilege changes during unit testing.
Runtime kernel assertions are injectable here solely to validate rejection.
"""
from pathlib import Path
import hashlib
import json
import os
import socket
import stat
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_saveas_fd_worker as worker

FIXTURE = "gd31j6_writer.docx"
SESSION = "102030405060708090a0b0c0d0e0f010"
TAB = "f010e0d0c0b0a0908070605040302010"
BYTES = b"GETDOC-GD31J27L-SYNTHETIC-FD-ONLY"
DIGEST = hashlib.sha256(BYTES).hexdigest()


def manifest(**overrides):
    data = {
        "schema": worker.REQUEST_SCHEMA,
        "operation": "inspect_only",
        "fixtureName": FIXTURE,
        "sessionId": SESSION,
        "tabId": TAB,
        "sourceSha256": DIGEST,
        "expectedFinalName": "gd31j25_writer_saved.docx",
        "laboratoryOnly": True,
    }
    data.update(overrides)
    return (json.dumps(data, separators=(",", ":")) + "\n").encode("ascii")


def kernel():
    return {
        "NoNewPrivs": "1",
        "Seccomp": "2",
        "CapEff": "0000000000000000",
    }


class FdWorkerProtocolTests(unittest.TestCase):
    def test_exact_request_without_paths_permissions_or_write_operation(self):
        validated = worker.parse_request(manifest())
        self.assertEqual(FIXTURE, validated["fixtureName"])
        self.assertEqual(DIGEST, validated["sourceSha256"])
        self.assertEqual("inspect_only", validated["operation"])

    def test_forgeries_and_ambiguous_json_fail_closed(self):
        for raw in (
            manifest(operation="publish"),
            manifest(expectedFinalName="../contract.docx"),
            manifest(fixtureName="customer.docx"),
            manifest(laboratoryOnly=False),
            manifest(sourceSha256="0"*64),
            manifest(sessionId=TAB),
            manifest(tabId=SESSION),
            manifest(extraField="trusted"),
            manifest()[:-1],
            manifest() + manifest(),
            b'{"schema":"a","schema":"b"}\n',
            b"x" * (worker.MAX_REQUEST_BYTES + 1),
            manifest().replace(b'"inspect_only"', b'"write_user_file"'),
        ):
            with self.subTest(raw=raw[:50]), self.assertRaises(worker.WorkerError):
                worker.parse_request(raw)

    def test_prerequisites_require_actual_distinct_uid_and_namespaces(self):
        worker.validate_kernel_prerequisites(
            kernel(), worker_uid=40001, peer_uid=40002,
            worker_namespaces={"mnt": 100, "net": 200},
            peer_namespaces={"mnt": 101, "net": 201})
        for violation in ("root", "same_uid", "caps", "no_new_privs",
                          "seccomp", "same_mount", "same_network", "unknown"):
            with self.subTest(violation=violation):
                k = kernel()
                uid, peer_uid = 40001, 40002
                own, peer = {"mnt": 100, "net": 200}, {"mnt": 101, "net": 201}
                if violation == "root": uid = 0
                if violation == "same_uid": peer_uid = uid
                if violation == "caps": k["CapEff"] = "0000000000000001"
                if violation == "no_new_privs": k["NoNewPrivs"] = "0"
                if violation == "seccomp": k["Seccomp"] = "0"
                if violation == "same_mount": peer["mnt"] = own["mnt"]
                if violation == "same_network": peer["net"] = own["net"]
                if violation == "unknown": k["Unexpected"] = "yes"
                with self.assertRaises(worker.WorkerError):
                    worker.validate_kernel_prerequisites(
                        k, worker_uid=uid, peer_uid=peer_uid,
                        worker_namespaces=own, peer_namespaces=peer)

    def test_fd_roles_only_exact_owned_synthetic_directory_and_readonly_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            private = root / "gd31j26-private-ABCDEF"
            output = root / "gd31j25-output"
            private.mkdir(mode=0o700)
            output.mkdir(mode=0o700)
            (private / worker.PRIVATE_MARKER).write_bytes(worker.PRIVATE_BYTES)
            (output / worker.OUTPUT_MARKER).write_bytes(worker.OUTPUT_BYTES)
            input_file = root / FIXTURE
            input_file.write_bytes(BYTES)
            src = os.open(input_file, os.O_RDONLY | os.O_CLOEXEC)
            src_dir = os.open(private, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            out_dir = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            try:
                data = worker.inspect_fds((src, src_dir, out_dir),
                                          worker.parse_request(manifest()),
                                          worker_uid=os.geteuid())
                self.assertEqual(DIGEST, data["sourceSha256"])
                self.assertFalse(data["writePermissionGranted"])
                self.assertEqual("inspect_only", data["operation"])
                (output / "gd31j25_writer_saved.docx").write_bytes(b"EXISTING")
                with self.assertRaises(worker.WorkerError):
                    worker.inspect_fds((src, src_dir, out_dir),
                                       worker.parse_request(manifest()),
                                       worker_uid=os.geteuid())
            finally:
                os.close(src); os.close(src_dir); os.close(out_dir)

    def test_source_writer_fd_and_group_writable_private_dir_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            private = root / "gd31j26-private-ABCDEF"
            output = root / "gd31j25-output"
            private.mkdir(mode=0o700); output.mkdir(mode=0o700)
            (private / worker.PRIVATE_MARKER).write_bytes(worker.PRIVATE_BYTES)
            (output / worker.OUTPUT_MARKER).write_bytes(worker.OUTPUT_BYTES)
            src_path = root / FIXTURE
            src_path.write_bytes(BYTES)
            src = os.open(src_path, os.O_RDWR | os.O_CLOEXEC)
            scratch_fd = os.open(private, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            output_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            try:
                with self.assertRaises(worker.WorkerError):
                    worker.inspect_fds((src, scratch_fd, output_fd),
                                       worker.parse_request(manifest()),
                                       worker_uid=os.geteuid())
                os.close(src)
                src = os.open(src_path, os.O_RDONLY | os.O_CLOEXEC)
                private.chmod(0o770)
                with self.assertRaises(worker.WorkerError):
                    worker.inspect_fds((src, scratch_fd, output_fd),
                                       worker.parse_request(manifest()),
                                       worker_uid=os.geteuid())
            finally:
                os.close(src); os.close(scratch_fd); os.close(output_fd)

    def test_denial_response_never_grants_production(self):
        response = worker.diagnostic(
            worker.parse_request(manifest()),
            "transport_preflight_not_attestation")
        for field in ("nativeSaveProven", "documentVerified",
                      "saveVerified", "approvedForProduction",
                      "writePermissionGranted", "releaseReady"):
            self.assertIs(response[field], False)
        self.assertEqual("denied", response["state"])


if __name__ == "__main__":
    unittest.main()
