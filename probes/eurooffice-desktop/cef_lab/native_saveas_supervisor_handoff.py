#!/usr/bin/env python3
"""GD31J27o — read-only supervisor handoff observation, NEVER a launcher.

Observes SO_PEERCRED, /proc/<pid>/status, mountinfo, ns and open FD slots.
No process spawning, privileged setup, IPC rights transfer or file writes.
Even when ALL observations match, results are local diagnostics rather than
security attestations. procfs may deny inspection across UIDs: fail closed,
never weaken isolation to make this observer pass.
"""
from __future__ import annotations

import os
import re
import socket
import stat
import struct
import sys

import native_saveas_sandbox_observer as kernel_observer

SNAPSHOT_SCHEMA = "getdoc-gd31j27o-linux-fd3-supervisor-observation-lab/1"
RESULT_SCHEMA = "getdoc-gd31j27o-linux-supervisor-review-denial-lab/1"
SOCKET_TARGET = re.compile(r"socket:\[[1-9][0-9]{0,19}\]\Z", re.ASCII)
PIPE_TARGET = re.compile(r"pipe:\[[1-9][0-9]{0,19}\]\Z", re.ASCII)
STD_TARGET = re.compile(r"/dev/null\Z", re.ASCII)
MAX_WORKER_FDS = 4


class HandoffError(ValueError):
    """Never disclose process paths, private fd targets, or business data."""


def _exact(value, names):
    if type(value) is not dict or set(value) != set(names):
        raise HandoffError("Supervisor handoff schema mismatch")


def default_handoff_gate():
    return {
        "schema": RESULT_SCHEMA,
        "state": "blocked",
        "reviewState": "requires-independent-review",
        "observedWorkerFds": 0,
        "observedFd3Socket": False,
        "launchAuthorized": False,
        "kernelIsolationAttested": False,
        "brokerHandshakeAttested": False,
        "nativePublicationAuthorized": False,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "releaseReady": False,
        "writePermissionGranted": False,
        "safeToDeleteAutomatically": False,
        "laboratoryOnly": True,
    }


def inspect_snapshot(data: dict):
    """Only parse bounded, observed facts. Never promote to 'approved'."""
    _exact(data, {
        "schema", "workerPid", "workerUid", "brokerUid",
        "observedPeerPid", "observedPeerUid", "observedPeerGid",
        "workerStatus", "workerMountinfo",
        "workerNamespaces", "brokerNamespaces", "workerFdTargets",
    })
    worker_pid = data["workerPid"]
    worker_uid = data["workerUid"]
    broker_uid = data["brokerUid"]
    if (data["schema"] != SNAPSHOT_SCHEMA or
            type(worker_pid) is not int or not 1 <= worker_pid <= 2**31 - 1 or
            type(worker_uid) is not int or worker_uid <= 0 or
            type(broker_uid) is not int or broker_uid <= 0 or
            worker_uid == broker_uid or
            type(data["observedPeerPid"]) is not int or
            data["observedPeerPid"] != worker_pid or
            type(data["observedPeerUid"]) is not int or
            data["observedPeerUid"] != worker_uid or
            type(data["observedPeerGid"]) is not int or
            data["observedPeerGid"] <= 0):
        raise HandoffError("Worker/broker process identity is not pinned")

    fd_targets = data["workerFdTargets"]
    _exact(fd_targets, {"0", "1", "2", "3"})
    if any(type(target) is not str or len(target) > 80
           for target in fd_targets.values()):
        raise HandoffError("Worker inherited unrecognized descriptor")
    for key in ("0", "1", "2"):
        name = fd_targets[key]
        if not PIPE_TARGET.fullmatch(name) and not STD_TARGET.fullmatch(name):
            raise HandoffError("Worker inherited unexpected stdio descriptor")
    if not SOCKET_TARGET.fullmatch(fd_targets["3"]):
        raise HandoffError("Worker FD3 is not a Unix socket descriptor")
    try:
        kernel_observer.inspect_observed(
            status_text=data["workerStatus"],
            mountinfo_text=data["workerMountinfo"],
            own_ns=data["workerNamespaces"],
            peer_ns=data["brokerNamespaces"],
            expected_uid=worker_uid,
            peer_uid=broker_uid,
        )
    except kernel_observer.ObservationError as exc:
        raise HandoffError("Kernel worker isolation prerequisites refused") from exc

    result = default_handoff_gate()
    result.update({
        "observedWorkerFds": MAX_WORKER_FDS,
        "observedFd3Socket": True,
        "state": "blocked",
        "reviewState": "requires-independent-review",
    })
    return result


def _read_linux_file(path: str, limit: int):
    try:
        fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        try:
            data = b""
            while len(data) <= limit:
                block = os.read(fd, min(8192, limit + 1 - len(data)))
                if not block:
                    break
                data += block
            if len(data) > limit:
                raise HandoffError("Bounded Linux kernel evidence exceeded")
            return data.decode("ascii", "strict")
        finally:
            os.close(fd)
    except (OSError, UnicodeError, ValueError) as exc:
        if isinstance(exc, HandoffError):
            raise
        raise HandoffError("Linux kernel evidence inaccessible") from exc


def _proc_start_ticks(pid: int):
    data = _read_linux_file(f"/proc/{pid}/stat", 8192)
    # stat comm is enclosed in parentheses, and itself may contain spaces or ')'.
    pos = data.rfind(") ")
    if pos < 0:
        raise HandoffError("Unparseable Linux process identity")
    # Field 3 is the first token after comm; starttime is overall field 22.
    fields = data[pos + 2:].split()
    if len(fields) < 20 or not fields[19].isdecimal():
        raise HandoffError("Unparseable Linux process start time")
    ticks = int(fields[19])
    if ticks <= 0:
        raise HandoffError("Invalid Linux process start time")
    return ticks


def _read_worker_fd_targets(pid: int):
    directory = f"/proc/{pid}/fd"
    targets = {}
    try:
        with os.scandir(directory) as scanner:
            entries = list(scanner)
        if len(entries) != MAX_WORKER_FDS:
            raise HandoffError("Worker has unexpected inherited descriptors")
        for entry in entries:
            if entry.name not in ("0", "1", "2", "3"):
                raise HandoffError("Worker FD table is not an exact sandbox allowlist")
            targets[entry.name] = os.readlink(entry.path)
    except OSError as exc:
        raise HandoffError("Worker descriptor inspection unavailable") from exc
    return targets


def read_actual_handoff(sock: socket.socket, *,
                        expected_worker_pid: int, expected_worker_uid: int,
                        broker_uid: int, expected_worker_start_ticks: int):
    """Audit externally provisioned peer, before broker sends SCM_RIGHTS.

    The caller MUST obtain expected_worker_pid/uid/start_ticks from an
    independent trusted supervisor. If /proc denies cross-UID reads, refuse.
    This function must never enable writing based only on its returned result.
    """
    if (not sys.platform.startswith("linux") or
            type(expected_worker_start_ticks) is not int or
            expected_worker_start_ticks <= 0 or
            type(expected_worker_pid) is not int or expected_worker_pid <= 0 or
            type(expected_worker_uid) is not int or expected_worker_uid <= 0 or
            type(broker_uid) is not int or broker_uid <= 0 or
            broker_uid != os.geteuid() or
            sock.family != socket.AF_UNIX or
            sock.type & 0xF != socket.SOCK_SEQPACKET):
        raise HandoffError("External Linux worker session not confirmed")
    try:
        credentials = sock.getsockopt(
            socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
        pid, uid, gid = struct.unpack("3i", credentials)
    except (OSError, ValueError, struct.error) as exc:
        raise HandoffError("Kernel worker peer credentials unavailable") from exc
    if (pid != expected_worker_pid or uid != expected_worker_uid or
            uid == broker_uid or gid <= 0):
        raise HandoffError("SO_PEERCRED does not match separately pinned worker")
    if _proc_start_ticks(pid) != expected_worker_start_ticks:
        raise HandoffError("Worker process lifetime does not match approved launch")
    try:
        namespaces = {k: os.stat(f"/proc/{pid}/ns/{k}").st_ino
                      for k in ("mnt", "net")}
        broker_ns = {k: os.stat(f"/proc/self/ns/{k}").st_ino
                     for k in ("mnt", "net")}
    except OSError as exc:
        raise HandoffError("Worker namespace inspection unavailable") from exc
    evidence = {
        "schema": SNAPSHOT_SCHEMA,
        "workerPid": pid,
        "workerUid": uid,
        "brokerUid": broker_uid,
        "observedPeerPid": pid,
        "observedPeerUid": uid,
        "observedPeerGid": gid,
        "workerStatus": _read_linux_file(
            f"/proc/{pid}/status", kernel_observer.MAX_STATUS),
        "workerMountinfo": _read_linux_file(
            f"/proc/{pid}/mountinfo", kernel_observer.MAX_MOUNTINFO),
        "workerNamespaces": namespaces,
        "brokerNamespaces": broker_ns,
        "workerFdTargets": _read_worker_fd_targets(pid),
    }
    report = inspect_snapshot(evidence)
    if _proc_start_ticks(pid) != expected_worker_start_ticks:
        raise HandoffError("Worker PID changed during inspection")
    return report
