#!/usr/bin/env python3
"""GD31J27r: preopened worker-owned FD intake / single-use deny-only relay.

Internal unprivileged broker API, NOT a privileged supervisor, launcher,
CEF integration or production SaveAs handler. An independently audited root
supervisor will pass three O_RDONLY capabilities through a connected Unix
SOCK_SEQPACKET. Broker may fstat worker-owned 0700 dirfds but cannot
traverse/create inside them. Worker independently verifies source and markers.

The deployed GD31J27p listener still sends zero FDs; relay remains unwired.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True
import array
import os
import socket
if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl = None  # Source-only tests can import; runtime is Linux-only.
import stat
import struct

import native_saveas_fd_worker as worker
import native_saveas_fd_broker as broker

ROLE_COUNT = 3
SCHEMA = "getdoc-gd31j27r-preopened-fd-relay-denied-lab/1"


class ProvisioningError(ValueError):
    """No user paths, fixture content or kernel details in diagnostics."""


def close_received_fds(handles):
    for fd in handles:
        try:
            os.close(fd)
        except OSError:
            pass


def verify_pinned_handles(handles, request, *, worker_uid, independent_fixture_hashes):
    """Read-only worker-owned FD audit, no openat inside 0700 dirs."""
    if (not sys.platform.startswith("linux") or
            type(worker_uid) is not int or worker_uid <= 0 or
            type(handles) not in (list, tuple) or len(handles) != ROLE_COUNT or
            any(type(fd) is not int or fd < 0 for fd in handles) or
            len(set(handles)) != ROLE_COUNT or type(request) is not dict or
            request.get("operation") != "inspect_only" or
            request.get("laboratoryOnly") is not True):
        raise ProvisioningError("Invalid worker-owned capability roles")
    if (type(independent_fixture_hashes) is not dict or
            set(independent_fixture_hashes) != set(worker.FIXTURES) or
            any(type(sha) is not str or not worker.SHA.fullmatch(sha) or
                sha == "0" * 64 for sha in independent_fixture_hashes.values()) or
            request["sourceSha256"] != independent_fixture_hashes[request["fixtureName"]]):
        raise ProvisioningError("Synthetic SHA not independently pinned")
    identities = []
    for i, fd in enumerate(handles):
        try:
            meta = os.fstat(fd)
            mode = fcntl.fcntl(fd, fcntl.F_GETFL)
            fd_flags = fcntl.fcntl(fd, fcntl.F_GETFD)
        except OSError as exc:
            raise ProvisioningError("FD metadata inaccessible") from exc
        if ((mode & os.O_ACCMODE) != os.O_RDONLY or
                not fd_flags & fcntl.FD_CLOEXEC or meta.st_uid != worker_uid):
            raise ProvisioningError("Received capability is not worker-owned read-only")
        if i == 0:
            if (not stat.S_ISREG(meta.st_mode) or meta.st_nlink != 1 or
                    meta.st_mode & 0o022 or
                    not 0 < meta.st_size <= worker.MAX_FILE_BYTES):
                raise ProvisioningError("Synthetic source metadata invalid")
        elif (not stat.S_ISDIR(meta.st_mode) or
              (meta.st_mode & 0o777) != 0o700 or meta.st_nlink < 2):
            raise ProvisioningError("Worker-owned directory permissions invalid")
        identities.append(meta)
    if (len({m.st_dev for m in identities}) != 1 or
            (identities[1].st_dev, identities[1].st_ino) ==
            (identities[2].st_dev, identities[2].st_ino)):
        raise ProvisioningError("Worker-owned directory roles overlap or cross devices")
    try:
        digest = worker._digest_open_fd(handles[0], identities[0])
    except (OSError, worker.WorkerError) as exc:
        raise ProvisioningError("Synthetic source digest unstable") from exc
    if digest != request["sourceSha256"]:
        raise ProvisioningError("Received synthetic bytes do not match pin")
    return {
        "schema": SCHEMA, "operation": "inspect_only",
        "sourceSha256": digest, "roleOwnership": "worker-owned-preopened",
        "state": "denied", "nativePublicationAuthorized": False,
        "writePermissionGranted": False, "releaseReady": False,
        "approvedForProduction": False, "laboratoryOnly": True,
    }


def _received_exact_three(sock):
    """Exactly one recvmsg, close all obtained FDs on ANY failure."""
    if not hasattr(socket, "MSG_CMSG_CLOEXEC"):
        raise ProvisioningError("Linux SCM_RIGHTS CLOEXEC unavailable")
    transferred = array.array("i")
    success = False
    try:
        payload, ancillary, flags, _ = sock.recvmsg(
            worker.MAX_REQUEST_BYTES + 1,
            socket.CMSG_SPACE(ROLE_COUNT * transferred.itemsize),
            socket.MSG_CMSG_CLOEXEC)
        for level, kind, data in ancillary:
            if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
                if len(data) % transferred.itemsize:
                    raise ProvisioningError("Misaligned SCM_RIGHTS control")
                transferred.frombytes(data)
            else:
                raise ProvisioningError("Unexpected SCM ancillary data")
        if ((flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC)) or
                len(transferred) != ROLE_COUNT or
                len(payload) > worker.MAX_REQUEST_BYTES or
                len(set(transferred)) != ROLE_COUNT):
            raise ProvisioningError("Truncated or unexpected FD capabilities")
        success = True
        return payload, list(transferred)
    except (OSError, socket.timeout) as exc:
        raise ProvisioningError("External root supervisor FD intake failed") from exc
    finally:
        if not success:
            close_received_fds(transferred)


def _supervisor_cred(sock, *, trusted_supervisor_uid, expected_supervisor_pid):
    if (not sys.platform.startswith("linux") or
            type(trusted_supervisor_uid) is not int or
            trusted_supervisor_uid != 0 or
            type(expected_supervisor_pid) is not int or
            expected_supervisor_pid <= 0 or
            sock.family != socket.AF_UNIX or
            sock.type & 0xF != socket.SOCK_SEQPACKET):
        raise ProvisioningError("External supervisor peer not pinned")
    try:
        credentials = sock.getsockopt(
            socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
        pid, uid, gid = struct.unpack("3i", credentials)
    except (OSError, ValueError, struct.error) as exc:
        raise ProvisioningError("Kernel supervisor credentials unavailable") from exc
    if pid != expected_supervisor_pid or uid != 0 or gid < 0:
        raise ProvisioningError("Root supervisor identity mismatch")


def receive_pinned_roles(supervisor_sock, *, trusted_supervisor_uid,
                         expected_supervisor_pid, worker_uid,
                         independent_fixture_hashes, approved_disposable_vm):
    """Single receipt from kernel-verified root peer; caller owns returned FDs.

    This gate is not attestation. Trusted supervisor is NOT provided in q/r.
    """
    if approved_disposable_vm is not True:
        raise ProvisioningError("Disposable offline VM only")
    _supervisor_cred(supervisor_sock,
                     trusted_supervisor_uid=trusted_supervisor_uid,
                     expected_supervisor_pid=expected_supervisor_pid)
    handles = []
    try:
        supervisor_sock.settimeout(5.0)
        payload, handles = _received_exact_three(supervisor_sock)
        try:
            request = worker.parse_request(payload)
        except worker.WorkerError as exc:
            raise ProvisioningError("Exact inspect-only synthetic request refused") from exc
        verify_pinned_handles(handles, request, worker_uid=worker_uid,
                              independent_fixture_hashes=independent_fixture_hashes)
        return payload, handles
    except BaseException:
        close_received_fds(handles)
        raise


def relay_pinned_once(session, supervisor_sock, worker_sock, *,
                      trusted_supervisor_uid, expected_supervisor_pid,
                      worker_uid, broker_uid, expected_worker_pid,
                      expected_worker_start_ticks, approved_disposable_vm,
                      independent_fixture_hashes):
    """One receive + one kernel-reviewed broker handoff. Always deny-only.

    No privileged launch, unshare, worker publication or cleanup.
    """
    if not isinstance(session, broker.BrokerSession) or session.consumed:
        raise ProvisioningError("Broker session consumed or invalid")
    handles = []
    try:
        payload, handles = receive_pinned_roles(
            supervisor_sock,
            trusted_supervisor_uid=trusted_supervisor_uid,
            expected_supervisor_pid=expected_supervisor_pid,
            worker_uid=worker_uid,
            independent_fixture_hashes=independent_fixture_hashes,
            approved_disposable_vm=approved_disposable_vm)
        if (type(broker_uid) is not int or broker_uid <= 0 or
                broker_uid != os.geteuid() or worker_uid == broker_uid):
            raise ProvisioningError("Separate nonroot broker/worker UID required")
        result = broker.handoff_after_kernel_review(
            session, worker_sock, payload, handles,
            broker_uid=broker_uid, expected_worker_uid=worker_uid,
            expected_worker_pid=expected_worker_pid,
            expected_worker_start_ticks=expected_worker_start_ticks,
            approved_disposable_vm=approved_disposable_vm,
            approved_fixture_hashes=independent_fixture_hashes)
        return {
            **result, "schema": SCHEMA, "state": "denied",
            "trustedSupervisorAttested": False,
            "nativePublicationAuthorized": False,
            "writePermissionGranted": False,
            "releaseReady": False, "approvedForProduction": False,
        }
    finally:
        close_received_fds(handles)
        session.consumed = True
