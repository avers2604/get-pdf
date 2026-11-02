#!/usr/bin/env python3
"""GD31J27p: real AF_UNIX SOCK_SEQPACKET broker listener AFTER broker UID setup.

Strictly one connected worker, then deny. No FD rights transferred, no
privileged operations, no SaveAs publication, no cleanup/unlink of existing
socket entries, no user paths, no production permissions. A trusted OS
service (currently blocked) MUST create a private /run directory before
launch, configure the broker UID/GID and set up a second worker service.

This is an executable handshake precursor, NOT the future privileged
sandbox supervisor or a complete broker forwarding synthetic OOXML.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import json
import os
import socket
import stat
import struct

import native_saveas_worker_dialer as dialer

SCHEMA = "getdoc-gd31j27p-broker-post-uid-denial-lab/1"


class ListenerError(ValueError):
    """Never echo socket paths, private files or contents in error text."""


def validate_broker_identity(policy, *, current_uid: int, current_gid: int):
    if (type(current_uid) is not int or
            type(current_gid) is not int or
            current_uid <= 0 or current_gid <= 0 or
            current_uid != policy["brokerUid"] or
            current_gid != policy["ipcGid"] or
            current_uid == policy["workerUid"]):
        raise ListenerError("Broker must be pre-launched under distinct nonroot UID")


def validate_worker_peer(*, pid: int, uid: int, gid: int, policy):
    if (type(pid) is not int or pid <= 0 or
            type(uid) is not int or uid <= 0 or
            type(gid) is not int or gid <= 0 or
            uid != policy["workerUid"] or
            gid != policy["workerGid"] or
            uid == policy["brokerUid"]):
        raise ListenerError("Worker connected with unexpected OS peer credentials")


def denial():
    return {
        "schema": SCHEMA,
        "state": "denied",
        "mode": "diagnostic-only",
        "launchAuthorized": False,
        "kernelIsolationAttested": False,
        "nativePublicationAuthorized": False,
        "nativeSaveProven": False,
        "approvedForProduction": False,
        "releaseReady": False,
        "writePermissionGranted": False,
        "laboratoryOnly": True,
    }


def _protected_parent(policy):
    try:
        meta = os.lstat(dialer.FIXED_SOCKET_DIR)
    except OSError as exc:
        raise ListenerError("Externally provisioned private IPC directory unavailable") from exc
    if (not stat.S_ISDIR(meta.st_mode) or
            meta.st_uid != policy["brokerUid"] or
            meta.st_gid != policy["ipcGid"] or
            (meta.st_mode & 0o777) != 0o750):
        raise ListenerError("Broker IPC directory must be owner-private and group-traversable")


def _absent_final_socket():
    try:
        os.lstat(dialer.FIXED_SOCKET)
    except FileNotFoundError:
        return
    except OSError as exc:
        raise ListenerError("Cannot inspect broker socket destination") from exc
    raise ListenerError("Broker socket destination already occupied; never unlink")


def _connected_worker_credentials(channel, policy):
    if (channel.family != socket.AF_UNIX or
            channel.type & 0xF != socket.SOCK_SEQPACKET):
        raise ListenerError("Worker connection must be Unix sequential packet")
    try:
        raw = channel.getsockopt(
            socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
        pid, uid, gid = struct.unpack("3i", raw)
    except (OSError, ValueError, struct.error) as exc:
        raise ListenerError("Cannot obtain worker peer credentials from kernel") from exc
    validate_worker_peer(pid=pid, uid=uid, gid=gid, policy=policy)


def listen_once(policy) -> int:
    validate_broker_identity(policy,
                             current_uid=os.geteuid(), current_gid=os.getegid())
    if os.getuid() != os.geteuid() or os.getgid() != os.getegid():
        raise ListenerError("Broker identities are not stable after OS launch")
    _protected_parent(policy)
    _absent_final_socket()
    with socket.socket(socket.AF_UNIX,
                       socket.SOCK_SEQPACKET | socket.SOCK_CLOEXEC) as listener:
        # File creation happens ONLY within the externally preowned private
        # /run directory. Never remove an existing pathname or follow symlinks.
        if listener.fileno() != 3:
            raise ListenerError("Broker listener must be the only FD above stdio")
        previous = os.umask(0o117)  # AF_UNIX socket mode: 0777 & ~0117 = 0660
        try:
            listener.bind(dialer.FIXED_SOCKET)
        finally:
            os.umask(previous)
        _protected_parent(policy)
        socket_meta = os.lstat(dialer.FIXED_SOCKET)
        dialer.validate_endpoint_meta(
            os.lstat(dialer.FIXED_SOCKET_DIR), socket_meta, policy)
        listener.listen(1)
        listener.settimeout(5.0)
        with listener.accept()[0] as channel:
            channel.settimeout(5.0)
            _connected_worker_credentials(channel, policy)
            # No request or rights are granted by this compatibility probe.
            # Worker rejects this zero-FD packet, sends its denial, exits 2.
            channel.sendall(b'{"state":"denied","laboratoryOnly":true}\n')
            try:
                channel.recv(512)
            except (OSError, socket.timeout):
                pass
    # Intentionally no unlink. OS RuntimeDirectory disposal is an ADMIN
    # responsibility; an ambiguous socket path must never be auto-cleaned.
    return 2


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    if args or not sys.platform.startswith("linux"):
        return 2
    try:
        policy = dialer._read_pinned_policy()
        return listen_once(policy)
    except (ListenerError, dialer.DialerError, OSError, ValueError, socket.timeout):
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
