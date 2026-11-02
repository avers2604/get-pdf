#!/usr/bin/env python3
"""GD31J27q: UNWIRED Linux synthetic-only anonymous snapshot publisher.

This function is NOT called by worker.serve_one, broker listener or GUI.
It writes only when directly invoked by an operator-approved, disposable,
offline VM test. Its explicit marker is a developer test opt-in, NOT security
attestation: a caller capable of running Python can forge it. The real trust
boundary requires separate UID/mount/FD provisioning and independent review.

Only strict fixture -> final basename mapping, three preopened FDs,
O_TMPFILE snapshot, verified procfs fd-bound no-replace hardlink, fsync.
Never use source name as publication link target. Never unlink after link:
post-link failure is an UNKNOWN final state and requires human review.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import ctypes
if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl = None
import hashlib
import json
import os
import stat

import native_saveas_fd_worker as worker

VM_TEST_ONLY_EXPLICIT_GATE = "GD31J27Q_DISPOSABLE_OFFLINE_VM_SYNTHETIC_ONLY"
PROC_SUPER_MAGIC = 0x9FA0
CHUNK = 64 * 1024


class SnapshotError(ValueError):
    """Fixed errors without source path, payload or corporate information."""


def _identity(meta):
    return (meta.st_dev, meta.st_ino, meta.st_size,
            meta.st_mtime_ns, meta.st_ctime_ns)


def _same_inode(first, second):
    return (first.st_dev, first.st_ino) == (second.st_dev, second.st_ino)


def _proc_directory():
    """Open true procfs, not a user-controlled directory masquerading as it."""
    if not sys.platform.startswith("linux"):
        raise SnapshotError("Linux procfs is required")
    try:
        fd = os.open("/proc/self/fd", os.O_RDONLY | os.O_DIRECTORY |
                     os.O_NOFOLLOW | os.O_CLOEXEC)
    except OSError as exc:
        raise SnapshotError("Kernel procfs FD directory unavailable") from exc
    try:
        # libc fstatfs writes a platform struct statfs into a sufficiently
        # large native buffer. Its first native long is f_type on Linux.
        libc = ctypes.CDLL(None, use_errno=True)
        fn = libc.fstatfs
        fn.argtypes = (ctypes.c_int, ctypes.c_void_p)
        fn.restype = ctypes.c_int
        buffer = ctypes.create_string_buffer(256)
        if fn(fd, buffer) != 0 or ctypes.c_long.from_buffer(buffer).value != PROC_SUPER_MAGIC:
            raise SnapshotError("Unverified kernel procfs mount")
        return fd
    except (OSError, AttributeError, TypeError, SnapshotError) as exc:
        os.close(fd)
        if isinstance(exc, SnapshotError):
            raise
        raise SnapshotError("Kernel procfs verification unavailable") from exc


def _snapshot_from_verified_source(source_fd, scratch_fd, expected_sha, original):
    """Return reader FD and closed writer; no name on disk until publication."""
    flags = getattr(os, "O_TMPFILE", 0)
    if not flags:
        raise SnapshotError("Anonymous files unavailable; no named fallback")
    writer = reader = -1
    try:
        try:
            writer = os.open(".", flags | os.O_RDWR | os.O_CLOEXEC, 0o600,
                             dir_fd=scratch_fd)
            empty = os.fstat(writer)
            if (not stat.S_ISREG(empty.st_mode) or empty.st_nlink != 0 or
                    empty.st_uid != os.geteuid() or empty.st_size != 0):
                raise SnapshotError("Anonymous snapshot identity invalid")
            sha = hashlib.sha256()
            position = 0
            while position < original.st_size:
                data = os.pread(source_fd, min(CHUNK, original.st_size-position), position)
                if not data:
                    raise SnapshotError("Synthetic source truncated during copy")
                sha.update(data)
                current = 0
                while current < len(data):
                    count = os.write(writer, data[current:])
                    if count <= 0:
                        raise SnapshotError("Incomplete anonymous snapshot write")
                    current += count
                position += len(data)
            if sha.hexdigest() != expected_sha:
                raise SnapshotError("Synthetic source did not match pinned digest")
            if _identity(os.fstat(source_fd)) != _identity(original):
                raise SnapshotError("Synthetic source changed during snapshot")
            if os.fstat(writer).st_size != original.st_size:
                raise SnapshotError("Anonymous snapshot differs in length")
            os.fsync(writer)
            os.fchmod(writer, 0o400)
            os.fsync(writer)
            procfd = _proc_directory()
            try:
                reader = os.open(str(writer), os.O_RDONLY | os.O_CLOEXEC |
                                 os.O_NONBLOCK, dir_fd=procfd)
            finally:
                os.close(procfd)
            opened = os.fstat(reader)
            if (not _same_inode(empty, opened) or opened.st_nlink != 0 or
                    (opened.st_mode & 0o777) != 0o400 or
                    fcntl.fcntl(reader, fcntl.F_GETFL) & os.O_ACCMODE != os.O_RDONLY):
                raise SnapshotError("Anonymous read-only FD verification failed")
            os.close(writer)
            writer = -1  # Writer must be closed BEFORE final becomes visible.
            return reader, opened
        except OSError as exc:
            raise SnapshotError("Anonymous snapshot syscall failed") from exc
    except BaseException:
        if reader >= 0:
            os.close(reader)
        raise
    finally:
        if writer >= 0:
            os.close(writer)


def _link_held_snapshot(read_fd, output_fd, final_name):
    """Linux procfs fd magic-link refers to verified anonymous inode."""
    procfd = _proc_directory()
    try:
        # follow_symlinks=True is required for /proc/self/fd/<open fd>.
        # Source is an FD number, never scratch basename.
        os.link(str(read_fd), final_name, src_dir_fd=procfd,
                dst_dir_fd=output_fd, follow_symlinks=True)
    except OSError as exc:
        # EEXIST: never replace. EPERM/EXDEV/ENOENT: fail closed.
        raise SnapshotError("Exclusive anonymous publication failed") from exc
    finally:
        os.close(procfd)


def publish_disposable_fixture(handles, request, *, worker_uid,
                               operator_vm_gate=None):
    """Internal-only lab tool; not reachable from IPC, CLI or production.

    The operator_vm_gate is a reminder for a disposable VM test, not a
    privilege token. Worker.serve_one deliberately never calls this.
    """
    if (operator_vm_gate != VM_TEST_ONLY_EXPLICIT_GATE or
            not sys.platform.startswith("linux") or
            not hasattr(os, "O_TMPFILE") or
            os.geteuid() == 0 or
            type(worker_uid) is not int or worker_uid != os.geteuid() or
            type(request) is not dict):
        raise SnapshotError("Offline disposable VM publication not authorized")
    # Strictly reuse the worker's untrusted input schema; INSPECT_ONLY input
    # does not imply any save permission. This function is NOT wired to it.
    try:
        serialized = (json.dumps(request, separators=(",", ":"))+"\n").encode("ascii")
        parsed = worker.parse_request(serialized)
        worker.inspect_fds(handles, parsed, worker_uid=worker_uid)
    except (worker.WorkerError, ValueError, UnicodeError, TypeError, OSError) as exc:
        raise SnapshotError("Synthetic FD preflight failed") from exc

    source_fd, scratch_fd, output_fd = handles
    try:
        source = os.fstat(source_fd)
        named = os.stat(parsed["fixtureName"], dir_fd=scratch_fd,
                        follow_symlinks=False)
        if (not stat.S_ISREG(named.st_mode) or
                not _same_inode(source, named) or
                source.st_uid != worker_uid or
                source.st_nlink != 1 or
                source.st_size <= 0 or
                source.st_size > worker.MAX_FILE_BYTES):
            raise SnapshotError("Synthetic source path does not match approved FD")
        # File content must be durable BEFORE a separate snapshot is published.
        os.fsync(source_fd)
        if _identity(os.fstat(source_fd)) != _identity(source):
            raise SnapshotError("Synthetic source changed after fsync")
    except OSError as exc:
        raise SnapshotError("Synthetic source identity syscall failed") from exc

    reader = -1
    linked = False
    try:
        reader, opened = _snapshot_from_verified_source(
            source_fd, scratch_fd, parsed["sourceSha256"], source)
        if (_identity(os.fstat(source_fd)) != _identity(source) or
                not _same_inode(os.stat(parsed["fixtureName"],
                                        dir_fd=scratch_fd,
                                        follow_symlinks=False), source)):
            raise SnapshotError("Synthetic source changed before publication")
        # Exclusive final name; no automatic deletion on ANY post-link error.
        _link_held_snapshot(reader, output_fd, parsed["expectedFinalName"])
        linked = True
        published = os.stat(parsed["expectedFinalName"], dir_fd=output_fd,
                            follow_symlinks=False)
        current = os.fstat(reader)
        if (not stat.S_ISREG(published.st_mode) or
                not _same_inode(current, published) or
                not _same_inode(opened, current) or
                current.st_nlink != 1 or published.st_nlink != 1 or
                current.st_size != source.st_size or
                (published.st_mode & 0o777) != 0o400 or
                worker._digest_open_fd(reader, current) != parsed["sourceSha256"]):
            raise SnapshotError("Post-link synthetic snapshot review required")
        os.fsync(output_fd)
        return {
            "schema": "getdoc-gd31j27q-synthetic-worker-publication-lab/1",
            "state": "synthetic-link-created-not-approved",
            "sha256": parsed["sourceSha256"],
            "reviewRequired": True,
            "nativePublicationAuthorized": False,
            "nativeSaveProven": False,
            "documentVerified": False,
            "saveVerified": False,
            "approvedForProduction": False,
            "releaseReady": False,
            "writePermissionGranted": False,
            "laboratoryOnly": True,
        }
    except OSError as exc:
        # A syscall can fail before or after the hardlink becomes visible;
        # never unlink a possibly different or partially durable final name.
        raise SnapshotError("Synthetic publication uncertain; manual review required") from exc
    finally:
        if reader >= 0:
            os.close(reader)
        # 'linked' exists for human audit only; automatic cleanup forbidden.
        _ = linked
