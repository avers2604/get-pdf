#!/usr/bin/env python3
"""GD31J27m: unprivileged, DENY-ONLY handoff to an externally isolated worker.

No process creation, setuid, unshare, mount, namespace changes, files opened by
path, output writes, shell, network, or release authority. The administrator
must provision the distinct-UID worker AND give the broker preopened FDs and a
preconnected AF_UNIX/SOCK_SEQPACKET. It is NOT an OS sandbox launcher.

This module deliberately cannot request publication. One packet, one response,
one-use BrokerSession. Worker responses are diagnostics ONLY: even a correct
kernel preflight can never grant real SaveAs or production rights.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import array
import json
import os
import socket
import stat
import struct

if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl = None

import native_saveas_fd_worker as worker
import native_saveas_supervisor_handoff as supervisor_handoff
import native_saveas_pidfd_provenance as pidfd
import native_saveas_socket_peerpidfd as socket_peer

BROKER_SCHEMA = "getdoc-gd31j27m-external-fd-broker-denial-lab/1"
MAX_REPLY_BYTES = 2048
MAX_SEND_BYTES = worker.MAX_REQUEST_BYTES


class BrokerError(ValueError):
    """Fixed generic diagnostics; never print FD paths or request contents."""


def _valid_fd_roles(handles):
    if (type(handles) not in (tuple, list) or len(handles) != worker.ROLE_COUNT or
            len(set(handles)) != worker.ROLE_COUNT or
            any(type(fd) is not int or fd < 0 for fd in handles)):
        raise BrokerError("Exactly three distinct nonnegative FD roles required")
    info = []
    for i, fd in enumerate(handles):
        try:
            metadata = os.fstat(fd)
            flags = fcntl.fcntl(fd, fcntl.F_GETFL)
            desc = fcntl.fcntl(fd, fcntl.F_GETFD)
        except (OSError, TypeError) as exc:
            raise BrokerError("Broker received a bad descriptor") from exc
        if (flags & os.O_ACCMODE) != os.O_RDONLY or
                not desc & fcntl.FD_CLOEXEC):
            raise BrokerError("Broker FD not read-only and close-on-exec")
        if i == 0:
            if (not stat.S_ISREG(metadata.st_mode) or
                    metadata.st_nlink != 1 or metadata.st_mode & 0o022 or
                    not 0 < metadata.st_size <= worker.MAX_FILE_BYTES):
                raise BrokerError("Source FD is not an approved synthetic regular file")
        else:
            if (not stat.S_ISDIR(metadata.st_mode) or
                    (metadata.st_mode & 0o777) != 0o700):
                raise BrokerError("Directory FD is not private")
        info.append(metadata)
    if (info[1].st_dev != info[2].st_dev or
            info[0].st_dev != info[1].st_dev or
            (info[1].st_dev, info[1].st_ino) ==
            (info[2].st_dev, info[2].st_ino)):
        raise BrokerError("The FD roles do not belong to one isolated filesystem")
    return info


def _exact_reply(raw: bytes, request: dict):
    if (type(raw) is not bytes or not 2 <= len(raw) <= MAX_REPLY_BYTES or
            raw.count(b"\n") != 1 or not raw.endswith(b"\n")):
        raise BrokerError("Malformed bounded worker denial reply")
    try:
        fields = json.loads(raw[:-1].decode("ascii", "strict"),
                            object_pairs_hook=worker._unique)
    except (ValueError, UnicodeError, RecursionError) as exc:
        raise BrokerError("Invalid worker denial reply") from exc
    if type(fields) is not dict or fields.get("state") != "denied" or (
            fields.get("laboratoryOnly") is not True):
        raise BrokerError("Worker returned an invalid or privileged result")
    minimal = {"state", "laboratoryOnly"}
    complete = {"schema", "sessionId", "tabId", "fixtureName", "state",
                "failureCode", "nativeSaveProven", "documentVerified",
                "saveVerified", "approvedForProduction",
                "writePermissionGranted", "releaseReady", "laboratoryOnly"}
    if set(fields) == minimal:
        return
    if set(fields) != complete or (
            fields["schema"] != worker.RESPONSE_SCHEMA or
            fields["sessionId"] != request["sessionId"] or
            fields["tabId"] != request["tabId"] or
            fields["fixtureName"] != request["fixtureName"] or
            type(fields["failureCode"]) is not str or
            len(fields["failureCode"]) > 64 or
            any(fields[k] is not False for k in (
                "nativeSaveProven", "documentVerified", "saveVerified",
                "approvedForProduction", "writePermissionGranted", "releaseReady"))):
        raise BrokerError("Worker reply could not be bound to denied session")


class BrokerSession:
    """One-shot only; never retry after sendmsg due to ambiguous worker state."""
    def __init__(self):
        self.consumed = False

    def handoff(self, sock: socket.socket, packet: bytes, fds, *,
                broker_uid: int, expected_worker_uid: int,
                approved_disposable_vm: bool, approved_fixture_hashes: dict):
        if self.consumed:
            raise BrokerError("Broker session already consumed")
        self.consumed = True   # Even a failed preflight is not replayable.
        if (sys.platform != "linux" or
                approved_disposable_vm is not True or
                type(broker_uid) is not int or
                type(expected_worker_uid) is not int or
                broker_uid <= 0 or expected_worker_uid <= 0 or
                expected_worker_uid == broker_uid or
                broker_uid != os.geteuid()):
            raise BrokerError("External VM/UID boundary not approved")
        try:
            request = worker.parse_request(packet)
        except worker.WorkerError as exc:
            raise BrokerError("Rejected exact synthetic worker request") from exc
        # Peer-provided SHA is NOT authority. The external trusted operator
        # must independently pin all three synthetic fixture digest values.
        if (type(approved_fixture_hashes) is not dict or
                set(approved_fixture_hashes) != set(worker.FIXTURES) or
                any(type(d) is not str or not worker.SHA.fullmatch(d) or
                    d == "0" * 64 for d in approved_fixture_hashes.values()) or
                request["sourceSha256"] != approved_fixture_hashes[request["fixtureName"]]):
            raise BrokerError("Synthetic fixture digest not independently pinned")
        if len(packet) > MAX_SEND_BYTES:
            raise BrokerError("Oversized broker request")
        _valid_fd_roles(fds)
        if (sock.family != socket.AF_UNIX or
                sock.type & 0xF != socket.SOCK_SEQPACKET):
            raise BrokerError("Only connected Unix seqpacket workers permitted")
        try:
            raw = sock.getsockopt(
                socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
            peer_pid, peer_uid, peer_gid = struct.unpack("3i", raw)
            if (peer_pid <= 0 or peer_gid < 0 or
                    peer_uid != expected_worker_uid):
                raise BrokerError("Untrusted sandbox worker credentials")
            sock.settimeout(5.0)
            transfer = array.array("i", fds)
            sent = sock.sendmsg([packet],
                                [(socket.SOL_SOCKET, socket.SCM_RIGHTS,
                                  transfer.tobytes())])
            if sent != len(packet):
                raise BrokerError("Incomplete non-retryable FD transfer")
            reply, ancillary, flags, _ = sock.recvmsg(MAX_REPLY_BYTES + 1, 0)
            if flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC) or ancillary:
                raise BrokerError("Unexpected worker reply ancillary data")
            _exact_reply(reply, request)
        except (OSError, ValueError, socket.timeout, struct.error) as exc:
            if isinstance(exc, BrokerError):
                raise
            raise BrokerError("External isolated worker communication failed") from exc
        # The caller may display the outcome, but MUST NOT interpret it as
        # write permission, a successful SaveAs or a release criterion.
        return {
            "schema": BROKER_SCHEMA,
            "state": "denied",
            "workerPreflightAttested": False,
            "nativeSaveProven": False,
            "documentVerified": False,
            "saveVerified": False,
            "releaseReady": False,
            "approvedForProduction": False,
            "writePermissionGranted": False,
            "laboratoryOnly": True,
        }


def handoff_after_kernel_review(session: BrokerSession, sock: socket.socket,
                                packet: bytes, fds, *,
                                broker_uid: int, expected_worker_uid: int,
                                expected_worker_pid: int,
                                expected_worker_start_ticks: int,
                                approved_disposable_vm: bool,
                                approved_fixture_hashes: dict):
    """Deny-only worker handoff with held kernel pidfd across SCM_RIGHTS.

    First pin the OS process identity. Then verify real kernel FD3/status/
    namespace facts (which can fail due to cross-UID /proc restrictions).
    Keep the pidfd open during sendmsg AND recvmsg. No PID-only fallback.
    A worker exit after sendmsg means UNKNOWN transfer, never a retry.
    """
    if not isinstance(session, BrokerSession) or session.consumed:
        raise BrokerError("External broker session unavailable or already consumed")
    pinned = None
    peer_reference = None
    try:
        # This pidfd originates from the socket's ACTUAL kernel peer.
        # A numeric PID pin alone cannot provide this link.
        peer_reference = socket_peer.pin_socket_peer(
            sock,
            expected_worker_pid=expected_worker_pid,
            expected_worker_uid=expected_worker_uid,
            broker_uid=broker_uid,
            expected_worker_start_ticks=expected_worker_start_ticks,
        )
        pinned = pidfd.pin_worker_process(
            expected_worker_pid, expected_worker_start_ticks)
        observation = supervisor_handoff.read_actual_handoff(
            sock,
            expected_worker_pid=expected_worker_pid,
            expected_worker_uid=expected_worker_uid,
            broker_uid=broker_uid,
            expected_worker_start_ticks=expected_worker_start_ticks,
        )
        if (type(observation) is not dict or
                observation.get("state") != "blocked" or
                observation.get("reviewState") != "requires-independent-review" or
                observation.get("observedWorkerFds") != 4 or
                observation.get("observedFd3Socket") is not True or
                any(observation.get(key) is not False for key in (
                    "launchAuthorized", "kernelIsolationAttested",
                    "brokerHandshakeAttested", "nativePublicationAuthorized",
                    "approvedForProduction", "writePermissionGranted",
                    "releaseReady"))):
            raise BrokerError("Kernel worker observation is not denial-only")
        peer_reference.assert_live()
        pinned.assert_live()
        result = session.handoff(
            sock, packet, fds,
            broker_uid=broker_uid,
            expected_worker_uid=expected_worker_uid,
            approved_disposable_vm=approved_disposable_vm,
            approved_fixture_hashes=approved_fixture_hashes,
        )
        # Handoff completed a single sendmsg and read one denied reply.
        # The same pidfd MUST still refer to a running process here.
        # Receiving "denied" is not proof of durable file creation.
        pinned.assert_live()
        peer_reference.assert_live()
        expected = {
            "schema": BROKER_SCHEMA,
            "state": "denied",
            "workerPreflightAttested": False,
            "nativeSaveProven": False,
            "documentVerified": False,
            "saveVerified": False,
            "releaseReady": False,
            "approvedForProduction": False,
            "writePermissionGranted": False,
            "laboratoryOnly": True,
        }
        if type(result) is not dict or result != expected:
            raise BrokerError("Unexpected worker handoff authority or denial schema")
        return {
            **result,
            "launchAuthorized": False,
            "kernelIsolationAttested": False,
            "brokerHandshakeAttested": False,
            "nativePublicationAuthorized": False,
        }
    except (pidfd.PidfdError, socket_peer.PeerPidfdError,
            supervisor_handoff.HandoffError, BrokerError,
            OSError, ValueError) as exc:
        session.consumed = True
        if isinstance(exc, BrokerError):
            raise
        raise BrokerError("Worker pidfd or kernel handoff refused; no replay") from exc
    finally:
        close_failed = False
        # Close BOTH references even if either close fails. No kernel
        # descriptor may outlive the one-shot diagnostic exchange.
        if pinned is not None:
            try:
                pinned.close()
            except (pidfd.PidfdError, OSError):
                close_failed = True
        if peer_reference is not None:
            try:
                peer_reference.close()
            except (socket_peer.PeerPidfdError, OSError):
                close_failed = True
        if close_failed:
            session.consumed = True
            raise BrokerError("Kernel process references could not be closed")
