#!/usr/bin/env python3
"""GD31J27x: socket-bound Linux SO_PEERPIDFD peer observation, DENY ONLY.

Kernel Linux >=6.5 supplies a pidfd for the ACTUAL AF_UNIX socket peer
using getsockopt(SOL_SOCKET, SO_PEERPIDFD). This avoids equating a mere
numeric PID with the identity of the connected endpoint. The returned fd
must be explicitly closed by the caller. Requires separately pinned PID,
UID and starttime; observes the same process before/after IPC.

The generic Linux UAPI value 77 is used ONLY on explicitly supported
architectures; any unsupported kernel/architecture or procfs restriction
is a hard refusal. This is a necessary process/socket observation, not
an executable fingerprint, sandbox certificate, FD3 pair proof, Astra MAC
attestation, permission to save, or systemd launch provenance.

No ptrace, pidfd_getfd, process spawn, OS privilege change or file writes.
"""
from __future__ import annotations

import os
import platform
import socket
import struct
import sys

if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl = None

import native_saveas_pidfd_provenance as pidfd
import native_saveas_root_supervisor_audit as audit

LINUX_SO_PEERPIDFD = 77
_SUPPORTED_ARCH = frozenset(("x86_64", "amd64", "aarch64", "i386", "i686", "riscv64"))
_UCRED_SIZE = struct.calcsize("3i")
_FD_SIZE = struct.calcsize("i")

class PeerPidfdError(ValueError):
    """No process paths, socket inode targets or confidential data."""


class PinnedSocketPeer:
    """Owns exactly one kernel SO_PEERPIDFD returned descriptor."""
    def __init__(self, fd, pid, start_ticks):
        self._fd = fd
        self.pid = pid
        self.start_ticks = start_ticks
        self.socketPeerAttested = False
        self.nativePublicationAuthorized = False
        self.releaseReady = False

    def fileno(self):
        if self._fd is None:
            raise PeerPidfdError("Closed kernel socket peer reference")
        return self._fd

    def assert_live(self):
        fd = self.fileno()
        try:
            start = audit.read_broker_start_ticks(self.pid)
            if start != self.start_ticks:
                raise PeerPidfdError("Socket peer process birth time changed")
            pidfd._poll_still_running(fd)
        except (audit.AuditError, pidfd.PidfdError, OSError) as exc:
            raise PeerPidfdError("Kernel socket peer liveness unobservable") from exc
        return False

    def close(self):
        if self._fd is not None:
            fd, self._fd = self._fd, None
            try:
                os.close(fd)
            except OSError as exc:
                raise PeerPidfdError("Cannot close kernel peer pidfd") from exc

    def __enter__(self):
        try:
            self.assert_live()
        except BaseException:
            self.close()
            raise
        return self

    def __exit__(self, exc_type, exc, tb):
        self.close()
        return False


def pin_socket_peer(sock, *, expected_worker_pid, expected_worker_uid,
                    broker_uid, expected_worker_start_ticks):
    """Return socket-owned peer pidfd held across a DENIAL-ONLY handoff.

    SO_PEERPIDFD binds the returned kernel reference to the actual peer
    of this socket. SO_PEERCRED and independent start ticks are checked
    too, but neither observation certifies FD3 mapping or executable code.
    """
    if (not sys.platform.startswith("linux") or fcntl is None or
            platform.machine().lower() not in _SUPPORTED_ARCH or
            type(expected_worker_pid) is not int or
            not 1 <= expected_worker_pid < 2**31 or
            type(expected_worker_uid) is not int or expected_worker_uid <= 0 or
            type(broker_uid) is not int or broker_uid <= 0 or
            broker_uid == expected_worker_uid or
            type(expected_worker_start_ticks) is not int or
            expected_worker_start_ticks <= 0 or
            sock.family != socket.AF_UNIX or
            sock.type & 0xF != socket.SOCK_SEQPACKET):
        raise PeerPidfdError("Socket peer kernel preconditions refused")
    try:
        raw_creds = sock.getsockopt(socket.SOL_SOCKET,
                                    socket.SO_PEERCRED, _UCRED_SIZE)
        if type(raw_creds) is not bytes or len(raw_creds) != _UCRED_SIZE:
            raise PeerPidfdError("Malformed Unix socket peer credentials")
        pid, uid, gid = struct.unpack("3i", raw_creds)
        if (pid != expected_worker_pid or uid != expected_worker_uid or
                gid <= 0):
            raise PeerPidfdError("Kernel socket peer is not the pinned worker")
        # The kernel installs a NEW pidfd in our descriptor table. It must
        # be closed on EVERY path, including on a malformed return.
        raw_fd = sock.getsockopt(socket.SOL_SOCKET,
                                  LINUX_SO_PEERPIDFD, _FD_SIZE)
        if type(raw_fd) is not bytes or len(raw_fd) != _FD_SIZE:
            raise PeerPidfdError("Kernel SO_PEERPIDFD returned invalid data")
        received_fd = struct.unpack("i", raw_fd)[0]
    except (OSError, ValueError, struct.error, AttributeError) as exc:
        if isinstance(exc, PeerPidfdError):
            raise
        raise PeerPidfdError("Socket-bound peer pidfd unavailable") from exc
    if received_fd < 3:
        # Never close standard input/output/error as a "bad pidfd".
        raise PeerPidfdError("Kernel peer pidfd not a safe owned descriptor")
    completed = False
    try:
        if not fcntl.fcntl(received_fd, fcntl.F_GETFD) & fcntl.FD_CLOEXEC:
            raise PeerPidfdError("Socket peer pidfd lacks close-on-exec")
        reference = PinnedSocketPeer(
            received_fd, expected_worker_pid, expected_worker_start_ticks)
        reference.assert_live()
        completed = True
        return reference
    except (OSError, ValueError) as exc:
        if isinstance(exc, PeerPidfdError):
            raise
        raise PeerPidfdError("Kernel socket peer lifetime invalid") from exc
    finally:
        if not completed:
            try:
                os.close(received_fd)
            except OSError:
                pass
