#!/usr/bin/env python3
"""GD31J27p: real post-UID Unix SOCK_SEQPACKET dialer, DENY-ONLY.

This process must already have been started by an approved external OS service
under its FINAL unprivileged worker UID/GID. It NEVER calls setuid, setgid,
systemd, subprocess, shell, mount, chroot, user namespaces or worker launch.

The exact root-owned policy and exact broker-owned socket under /run are read
by a bounded path-only preflight, then socket FD3 is connected AFTER UID setup.
The only transferred data is subsequently inspected by the existing deny-only
native_saveas_fd_worker. No SaveAs, output creation or release rights exist.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import json
import os
import re
import socket
import stat
import struct

import native_saveas_fd_worker as worker

CONFIG_SCHEMA = "getdoc-gd31j27p-post-uid-dialer-policy-lab/1"
RESULT_SCHEMA = "getdoc-gd31j27p-post-uid-dialer-denial-lab/1"
FIXED_CONFIG = "/etc/getdoc-lab/gd31j27p-offline-worker.json"
FIXED_SOCKET = "/run/getdoc-gd31j27p-disposable/worker.sock"
FIXED_SOCKET_DIR = "/run/getdoc-gd31j27p-disposable"
MAX_POLICY_BYTES = 1024


class DialerError(ValueError):
    """Only generic diagnostics; never show file contents or secret paths."""


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise DialerError("Repeated policy key")
        result[key] = value
    return result


def parse_static_policy(raw: bytes) -> dict:
    if (type(raw) is not bytes or
            not 2 <= len(raw) <= MAX_POLICY_BYTES or
            not raw.endswith(b"\n") or raw.count(b"\n") != 1 or
            b"\r" in raw):
        raise DialerError("Invalid bounded root-owned worker policy")
    try:
        cfg = json.loads(raw[:-1].decode("ascii", "strict"),
                         object_pairs_hook=_unique_pairs,
                         parse_constant=lambda _: (_ for _ in ()).throw(
                             DialerError("Invalid worker policy value")))
    except (UnicodeError, ValueError, RecursionError) as exc:
        if isinstance(exc, DialerError):
            raise
        raise DialerError("Malformed worker policy") from exc
    if (type(cfg) is not dict or
            set(cfg) != {"schema", "workerUid", "workerGid", "brokerUid",
                         "ipcGid", "socketPath", "mode", "laboratoryOnly",
                         "publishEnabled"}):
        raise DialerError("Unexpected worker policy fields")
    if (cfg["schema"] != CONFIG_SCHEMA or
            cfg["socketPath"] != FIXED_SOCKET or
            cfg["mode"] != "diagnostic-only" or
            cfg["laboratoryOnly"] is not True or
            cfg["publishEnabled"] is not False or
            any(type(cfg[k]) is not int or cfg[k] <= 0 for k in (
                "workerUid", "workerGid", "brokerUid", "ipcGid")) or
            cfg["workerUid"] == cfg["brokerUid"] or
            cfg["workerGid"] != cfg["ipcGid"]):
        raise DialerError("Unsafe worker identity or publication configuration")
    return cfg


def validate_policy_file_meta(meta):
    if (not stat.S_ISREG(meta.st_mode) or meta.st_uid != 0 or
            meta.st_nlink != 1 or meta.st_mode & 0o022 or
            not 2 <= meta.st_size <= MAX_POLICY_BYTES):
        raise DialerError("Worker policy must be a protected root-owned file")


def validate_endpoint_meta(directory, endpoint, cfg):
    """Exact uid/gid/mode needed to allow a worker to dial broker's socket."""
    if (not stat.S_ISDIR(directory.st_mode) or
            directory.st_uid != cfg["brokerUid"] or
            directory.st_gid != cfg["ipcGid"] or
            (directory.st_mode & 0o777) != 0o750 or
            not stat.S_ISSOCK(endpoint.st_mode) or
            endpoint.st_uid != cfg["brokerUid"] or
            endpoint.st_gid != cfg["ipcGid"] or
            (endpoint.st_mode & 0o777) != 0o660):
        raise DialerError("Broker Unix socket not protected by separate UID")


def validate_worker_identity(cfg, *, current_uid: int, current_gid: int):
    if (type(current_uid) is not int or current_uid <= 0 or
            type(current_gid) is not int or current_gid <= 0 or
            current_uid != cfg["workerUid"] or
            current_gid != cfg["workerGid"] or
            current_uid == cfg["brokerUid"]):
        raise DialerError("Worker must already be running under its separate UID")


def worker_identity_matches(cfg):
    try:
        validate_worker_identity(cfg, current_uid=os.geteuid(),
                                 current_gid=os.getegid())
        return os.getuid() == os.geteuid() and os.getgid() == os.getegid()
    except DialerError:
        return False


def _read_pinned_policy() -> dict:
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
    try:
        fd = os.open(FIXED_CONFIG, flags)
        with os.fdopen(fd, "rb") as stream:
            before = os.fstat(stream.fileno())
            validate_policy_file_meta(before)
            raw = stream.read(MAX_POLICY_BYTES + 1)
            after = os.fstat(stream.fileno())
            if (len(raw) != before.st_size or
                    (before.st_dev, before.st_ino, before.st_size,
                     before.st_mtime_ns, before.st_ctime_ns) !=
                    (after.st_dev, after.st_ino, after.st_size,
                     after.st_mtime_ns, after.st_ctime_ns)):
                raise DialerError("Root-owned policy changed during read")
    except OSError as exc:
        raise DialerError("Approved root-owned policy inaccessible") from exc
    return parse_static_policy(raw)


def _socket_metadata(cfg):
    try:
        # Neither directory nor socket may be a symlink. No arbitrary pathname
        # is received over the network, JSON document or CLI.
        directory = os.lstat(FIXED_SOCKET_DIR)
        endpoint = os.lstat(FIXED_SOCKET)
        validate_endpoint_meta(directory, endpoint, cfg)
    except OSError as exc:
        raise DialerError("Approved broker socket unavailable") from exc
    return (directory.st_dev, directory.st_ino, directory.st_mode,
            endpoint.st_dev, endpoint.st_ino, endpoint.st_mode)


def _only_expected_fds(allowed: set[int]):
    """Best-effort self-inspection; must not claim OS-level FD attestation."""
    try:
        # os.listdir can temporarily open one procfs iterator FD, already
        # closed when its result is inspected. Exclude stale closed slots.
        for filename in os.listdir("/proc/self/fd"):
            if not filename.isdecimal():
                raise DialerError("Unexpected worker descriptor metadata")
            fd = int(filename)
            try:
                os.fstat(fd)
            except OSError:
                continue
            if fd not in allowed:
                raise DialerError("Worker inherited unexpected descriptors")
        for fd in allowed:
            os.fstat(fd)
    except OSError as exc:
        raise DialerError("Exact worker FD inventory inaccessible") from exc


def denial() -> dict:
    return {
        "schema": RESULT_SCHEMA,
        "state": "denied",
        "launchAuthorized": False,
        "brokerHandshakeAttested": False,
        "nativePublicationAuthorized": False,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "releaseReady": False,
        "writePermissionGranted": False,
        "laboratoryOnly": True,
    }


def connect_and_inspect_once(cfg: dict) -> int:
    """Connect after UID switch; invoke only the existing deny-only worker."""
    if not sys.platform.startswith("linux") or not worker_identity_matches(cfg):
        raise DialerError("Worker UID/GID not independently enforced by OS")
    # On POSIX, the broker must be in a DISTINCT UID when it binds/listens.
    # Creating a socketpair before UID switch and inheriting it is forbidden.
    _only_expected_fds({0, 1, 2})
    before = _socket_metadata(cfg)
    channel = socket.socket(socket.AF_UNIX,
                            socket.SOCK_SEQPACKET | socket.SOCK_CLOEXEC)
    try:
        if channel.fileno() != 3:
            raise DialerError("Exactly FD3 must be available before broker connect")
        channel.settimeout(5.0)
        channel.connect(FIXED_SOCKET)
        if _socket_metadata(cfg) != before:
            raise DialerError("Broker socket replaced while connecting")
        credentials = channel.getsockopt(
            socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
        pid, peer_uid, peer_gid = struct.unpack("3i", credentials)
        if (pid <= 0 or peer_uid != cfg["brokerUid"] or
                peer_uid == os.geteuid() or peer_gid != cfg["ipcGid"]):
            raise DialerError("Connected broker credentials do not match static policy")
        _only_expected_fds({0, 1, 2, 3})
        # No new socketpair, extra process, writer fd or new output pathname.
        # The existing worker performs kernel preflight, receives three FDs,
        # inspects synthetic bytes and returns deny-only diagnostic.
        return worker.serve_one(channel)
    finally:
        channel.close()


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    if args or not sys.platform.startswith("linux"):
        return 2
    try:
        cfg = _read_pinned_policy()
        return connect_and_inspect_once(cfg)
    except (DialerError, OSError, ValueError, struct.error, socket.timeout):
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
