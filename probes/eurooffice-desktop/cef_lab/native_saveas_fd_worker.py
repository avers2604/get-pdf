#!/usr/bin/env python3
"""GD31J27l: DENY-ONLY Linux worker transport and FD isolation preflight.

This is an OS process entrypoint but deliberately NOT a SaveAs implementation.
A separately provisioned, privileged broker would pass an AF_UNIX
SOCK_SEQPACKET as fd 3; one request carries exactly three SCM_RIGHTS handles:
approved synthetic source (O_RDONLY), owner-private scratch dir (O_RDONLY),
and approved output dir (O_RDONLY). Neither a sender JSON nor kernel status
alone grants isolation/production authority. No write, delete, launch or
document publication operation exists in this file.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import array
import hashlib
import json
import os
import re
import socket
import stat
import struct
import native_saveas_sandbox_observer as kernel_observer
if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl = None  # Linux-only module, imported by offline source contracts.

REQUEST_SCHEMA = "getdoc-gd31j27l-native-fd-worker-inspect-lab/1"
RESPONSE_SCHEMA = "getdoc-gd31j27l-native-fd-worker-denial-lab/1"
MAX_REQUEST_BYTES = 2048
MAX_FILE_BYTES = 32 * 1024 * 1024
FIXTURES = {
    "gd31j6_writer.docx": "gd31j25_writer_saved.docx",
    "gd31j6_calc.xlsx": "gd31j25_calc_saved.xlsx",
    "gd31j6_impress.pptx": "gd31j25_impress_saved.pptx",
}
PRIVATE_MARKER = "gd31j27g-private-owner.marker"
OUTPUT_MARKER = "gd31j25-output-lab.marker"
PRIVATE_BYTES = b"GETDOC_GD31J27G_PRIVATE_SCRATCH_ONLY\n"
OUTPUT_BYTES = b"GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n"
SHA = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)
GUID = re.compile(r"[0-9a-f]{32}\Z", re.ASCII)
ROLE_COUNT = 3


class WorkerError(ValueError):
    """Fixed, non-sensitive error messages only. Never include source contents."""


def _unique(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise WorkerError("Duplicate request key")
        result[name] = value
    return result


def parse_request(raw: bytes) -> dict:
    if (type(raw) is not bytes or
            not 3 <= len(raw) <= MAX_REQUEST_BYTES or
            raw.count(b"\n") != 1 or not raw.endswith(b"\n") or
            b"\r" in raw):
        raise WorkerError("Expected one bounded worker packet")
    try:
        obj = json.loads(
            raw[:-1].decode("ascii", "strict"), object_pairs_hook=_unique,
            parse_constant=lambda _: (_ for _ in ()).throw(
                WorkerError("Non-finite request constant")))
    except (ValueError, UnicodeError, RecursionError) as exc:
        if isinstance(exc, WorkerError):
            raise
        raise WorkerError("Malformed worker request") from exc
    fields = {"schema", "operation", "fixtureName", "sessionId", "tabId",
              "sourceSha256", "expectedFinalName", "laboratoryOnly"}
    if type(obj) is not dict or set(obj) != fields:
        raise WorkerError("Worker request schema mismatch")
    fixture = obj["fixtureName"]
    sha = obj["sourceSha256"]
    a, b = obj["sessionId"], obj["tabId"]
    if (obj["schema"] != REQUEST_SCHEMA or obj["operation"] != "inspect_only" or
            type(fixture) is not str or fixture not in FIXTURES or
            obj["expectedFinalName"] != FIXTURES[fixture] or
            obj["laboratoryOnly"] is not True or
            type(sha) is not str or not SHA.fullmatch(sha) or sha == "0" * 64 or
            type(a) is not str or type(b) is not str or
            not GUID.fullmatch(a) or not GUID.fullmatch(b) or a == b):
        raise WorkerError("Worker request violates synthetic-only policy")
    return obj


def validate_kernel_prerequisites(status: dict, *,
                                  worker_uid: int, peer_uid: int,
                                  worker_namespaces: dict, peer_namespaces: dict):
    """Necessary (not sufficient) kernel facts; broker may still be malicious."""
    if (type(status) is not dict or
            set(status) != {"NoNewPrivs", "Seccomp", "CapEff"} or
            type(worker_uid) is not int or type(peer_uid) is not int or
            worker_uid <= 0 or peer_uid == worker_uid or peer_uid < 0 or
            status["NoNewPrivs"] != "1" or status["Seccomp"] != "2" or
            status["CapEff"] != "0000000000000000" or
            type(worker_namespaces) is not dict or
            type(peer_namespaces) is not dict or
            set(worker_namespaces) != {"mnt", "net"} or
            set(peer_namespaces) != {"mnt", "net"} or
            any(type(n) is not int or n <= 0 for n in
                (*worker_namespaces.values(), *peer_namespaces.values())) or
            any(worker_namespaces[k] == peer_namespaces[k]
                for k in ("mnt", "net"))):
        raise WorkerError("Worker OS isolation prerequisites not confirmed")


def _read_marker(directory_fd: int, name: str, expected: bytes, euid: int):
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
    try:
        fd = os.open(name, flags, dir_fd=directory_fd)
        try:
            before = os.fstat(fd)
            if (not stat.S_ISREG(before.st_mode) or before.st_uid != euid or
                    before.st_nlink != 1 or before.st_mode & 0o022 or
                    before.st_size != len(expected)):
                raise WorkerError("Invalid synthetic directory owner marker")
            data = os.read(fd, 128)
            after = os.fstat(fd)
            if (data != expected or
                    (before.st_dev, before.st_ino, before.st_mtime_ns,
                     before.st_ctime_ns, before.st_size) !=
                    (after.st_dev, after.st_ino, after.st_mtime_ns,
                     after.st_ctime_ns, after.st_size)):
                raise WorkerError("Synthetic marker changed or mismatched")
        finally:
            os.close(fd)
    except OSError as exc:
        raise WorkerError("Unable to verify synthetic directory marker") from exc


def _fd_check(fd: int, *, expect_dir: bool, euid: int):
    try:
        flags = fcntl.fcntl(fd, fcntl.F_GETFL)
        fd_flags = fcntl.fcntl(fd, fcntl.F_GETFD)
        meta = os.fstat(fd)
    except OSError as exc:
        raise WorkerError("Invalid received worker descriptor") from exc
    if (flags & os.O_ACCMODE) != os.O_RDONLY or not fd_flags & fcntl.FD_CLOEXEC:
        raise WorkerError("Writable or inheritable worker descriptor refused")
    if expect_dir:
        if (not stat.S_ISDIR(meta.st_mode) or meta.st_uid != euid or
                (meta.st_mode & 0o777) != 0o700 or meta.st_nlink < 2):
            raise WorkerError("Worker output directory not private to UID")
    else:
        if (not stat.S_ISREG(meta.st_mode) or meta.st_nlink != 1 or
                meta.st_mode & 0o022 or
                not 0 < meta.st_size <= MAX_FILE_BYTES):
            raise WorkerError("Synthetic input FD is not a stable regular file")
    return meta


def _digest_open_fd(fd: int, before):
    h = hashlib.sha256()
    total = 0
    while total < before.st_size:
        try:
            chunk = os.pread(fd, min(65536, before.st_size - total), total)
        except OSError as exc:
            raise WorkerError("Cannot read synthetic source descriptor") from exc
        if not chunk:
            raise WorkerError("Synthetic source truncated during read")
        h.update(chunk)
        total += len(chunk)
    after = os.fstat(fd)
    if (before.st_dev, before.st_ino, before.st_size,
        before.st_mtime_ns, before.st_ctime_ns) != (
        after.st_dev, after.st_ino, after.st_size,
        after.st_mtime_ns, after.st_ctime_ns):
        raise WorkerError("Synthetic source changed during FD check")
    return h.hexdigest()


def inspect_fds(handles, request, *, worker_uid: int):
    """Read-only checks. No output name is ever created by this stage."""
    if type(handles) not in (tuple, list) or len(handles) != ROLE_COUNT or (
            len(set(handles)) != ROLE_COUNT):
        raise WorkerError("Exactly three distinct worker handles required")
    source, scratch, output = handles
    src = _fd_check(source, expect_dir=False, euid=worker_uid)
    scratch_stat = _fd_check(scratch, expect_dir=True, euid=worker_uid)
    output_stat = _fd_check(output, expect_dir=True, euid=worker_uid)
    if ((scratch_stat.st_dev, scratch_stat.st_ino) ==
            (output_stat.st_dev, output_stat.st_ino) or
            scratch_stat.st_dev != output_stat.st_dev or
            src.st_dev != scratch_stat.st_dev):
        raise WorkerError("Worker input and output directory scope mismatch")
    _read_marker(scratch, PRIVATE_MARKER, PRIVATE_BYTES, worker_uid)
    _read_marker(output, OUTPUT_MARKER, OUTPUT_BYTES, worker_uid)
    name = request["expectedFinalName"]
    try:
        os.stat(name, dir_fd=output, follow_symlinks=False)
    except FileNotFoundError:
        pass
    except OSError as exc:
        raise WorkerError("Cannot check occupied synthetic final name") from exc
    else:
        raise WorkerError("Synthetic final name already exists")
    digest = _digest_open_fd(source, src)
    if digest != request["sourceSha256"]:
        raise WorkerError("Synthetic source FD hash mismatch")
    return {
        "operation": "inspect_only", "sourceSha256": digest,
        "writePermissionGranted": False, "laboratoryOnly": True,
    }


def diagnostic(request, reason: str):
    """Always deny; an IPC handoff is NOT independent approval."""
    return {
        "schema": RESPONSE_SCHEMA,
        "sessionId": request["sessionId"],
        "tabId": request["tabId"],
        "fixtureName": request["fixtureName"],
        "state": "denied",
        "failureCode": reason,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "writePermissionGranted": False,
        "releaseReady": False,
        "laboratoryOnly": True,
    }


def _linux_kernel_facts(peer_pid: int):
    try:
        with open("/proc/self/status", "r", encoding="ascii") as status_file:
            raw = status_file.read(32768)
        status = {}
        for line in raw.splitlines():
            key, _, value = line.partition(":")
            if key in {"NoNewPrivs", "Seccomp", "CapEff"}:
                status[key] = value.strip()
        own_ns = {k: os.stat(f"/proc/self/ns/{k}").st_ino
                  for k in ("mnt", "net")}
        peer_ns = {k: os.stat(f"/proc/{peer_pid}/ns/{k}").st_ino
                   for k in ("mnt", "net")}
    except (OSError, UnicodeError) as exc:
        raise WorkerError("Linux sandbox kernel inspection unavailable") from exc
    return status, own_ns, peer_ns


def _receive_exact_three(sock: socket.socket):
    fds = array.array("i")
    ancillary_size = socket.CMSG_SPACE(ROLE_COUNT * fds.itemsize)
    if not hasattr(socket, "MSG_CMSG_CLOEXEC"):
        raise WorkerError("SCM_RIGHTS CLOEXEC kernel support required")
    transferred = False
    try:
        payload, ancillary, flags, _ = sock.recvmsg(
            MAX_REQUEST_BYTES + 1, ancillary_size,
            socket.MSG_CMSG_CLOEXEC)
        for level, kind, data in ancillary:
            if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
                count = len(data) // fds.itemsize
                fds.frombytes(data[:count * fds.itemsize])
        if (flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC) or
                len(fds) != ROLE_COUNT or
                any(level != socket.SOL_SOCKET or kind != socket.SCM_RIGHTS
                    for level, kind, _ in ancillary)):
            raise WorkerError("Unexpected worker FD payload or truncated packet")
        transferred = True
        return payload, list(fds)
    except (OSError, socket.timeout) as exc:
        raise WorkerError("Worker socket packet unavailable") from exc
    finally:
        # Handles are transferred to the caller only on success. On any
        # failure close all received handles; the trusted broker owns none.
        if not transferred:
            for fd in fds:
                try:
                    os.close(fd)
                except OSError:
                    pass


def serve_one(sock: socket.socket):
    """Never execute publication, even when all kernel checks succeed."""
    request = None
    received = []
    try:
        if (sock.family != socket.AF_UNIX or
                sock.type & 0xF != socket.SOCK_SEQPACKET):
            raise WorkerError("Dedicated AF_UNIX SOCK_SEQPACKET required")
        credentials = sock.getsockopt(
            socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
        pid, peer_uid, _ = struct.unpack("3i", credentials)
        if pid <= 0:
            raise WorkerError("Peer identity not available")
        status, own_ns, peer_ns = _linux_kernel_facts(pid)
        validate_kernel_prerequisites(
            status, worker_uid=os.geteuid(), peer_uid=peer_uid,
            worker_namespaces=own_ns, peer_namespaces=peer_ns)
        # GD31J27n: observe CURRENT kernel state before accepting even one FD.
        # This does not attest the unknown BPF allowlist or private mount data.
        kernel_observer.inspect_current(peer_pid=pid, peer_uid=peer_uid)
        payload, received = _receive_exact_three(sock)
        request = parse_request(payload)
        inspect_fds(received, request, worker_uid=os.geteuid())
        # Lab only. No work to perform or capability to write.
        sock.sendall((json.dumps(
            diagnostic(request, "transport_preflight_not_attestation"),
            separators=(",", ":")) + "\n").encode("ascii"))
    except (WorkerError, OSError, ValueError, socket.timeout):
        try:
            sock.sendall(b'{"state":"denied","laboratoryOnly":true}\n')
        except OSError:
            pass
    finally:
        for fd in received:
            try:
                os.close(fd)
            except OSError:
                pass
    return 2  # ALWAYS deny; even positive preflight is NOT approval.


def main(argv=None):
    if argv is None:
        argv = sys.argv[1:]
    if argv or not sys.platform.startswith("linux"):
        return 2
    try:
        # This process is launched only by an EXTERNAL OS sandbox supervisor.
        # No privileged setuid/unshare actions are attempted in this script.
        with socket.socket(fileno=3) as sock:
            sock.settimeout(5.0)
            return serve_one(sock)
    except (OSError, ValueError):
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
