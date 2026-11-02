#!/usr/bin/env python3
"""GD31J27t — root-owned, fixed-path synthetic FD sender (UNWIRED).

This module has NO main, setuid, child-process launch, service installer,
network connection, mkdir/chown/unlink, output write or SaveAs operation.
An independently reviewed offline Astra/Linux VM supervisor would ALREADY
be launched under root and hold an ALREADY CONNECTED SOCK_SEQPACKET to the
separate-UID broker. The operator must supply an authenticated pinned
configuration read from FIXED_MANIFEST, not from a user document or IPC.

O_NOFOLLOW + directory dirfd walk is used for three disposable worker-owned
read-only descriptors. The source is verified by the existing deny-only
worker.inspect_fds code (including marker files, exact source name and SHA).
It sends only an INSPECT_ONLY packet plus exactly three SCM_RIGHTS FDs.
No acknowledgement of durable receipt or publication is possible here.
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

import native_saveas_fd_worker as worker
import native_saveas_root_supervisor_audit as audit

MANIFEST_SCHEMA = "getdoc-gd31j27t-root-supervisor-synthetic-manifest-lab/1"
RESULT_SCHEMA = "getdoc-gd31j27t-root-supervisor-send-unattested-lab/1"
VM_TEST_ONLY_GATE = "GD31J27T_DISPOSABLE_OFFLINE_VM_NO_CORPORATE_DATA"
MAX_MANIFEST_BYTES = 2048
FIXED_MANIFEST = "/etc/getdoc-lab/gd31j27t-root-supervisor.json"
FIXED_BASE = "/run/getdoc-gd31j27t-disposable"
SCRATCH = "gd31j26-private-ABCDEF"
OUTPUT = "gd31j25-output"


class SupervisorError(ValueError):
    """Never include files, credentials, paths or document contents in errors."""


def _unique(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise SupervisorError("Repeated root supervisor manifest field")
        obj[key] = value
    return obj


def validate_manifest(data: dict) -> dict:
    fields = {
        "schema", "workerUid", "brokerUid", "brokerGid",
        "expectedBrokerPid", "expectedBrokerStartTicks", "fixtureName", "fixtureDigests",
        "sessionId", "tabId", "operation", "publishEnabled",
        "laboratoryOnly",
    }
    if type(data) is not dict or set(data) != fields:
        raise SupervisorError("Unrecognized root supervisor manifest")
    numbers = ("workerUid", "brokerUid", "brokerGid", "expectedBrokerPid",
               "expectedBrokerStartTicks")
    if (data["schema"] != MANIFEST_SCHEMA or
            any(type(data[k]) is not int or data[k] <= 0 for k in numbers) or
            data["workerUid"] == data["brokerUid"] or
            data["operation"] != "inspect_only" or
            data["publishEnabled"] is not False or
            data["laboratoryOnly"] is not True or
            type(data["fixtureName"]) is not str or
            data["fixtureName"] not in worker.FIXTURES or
            type(data["fixtureDigests"]) is not dict or
            set(data["fixtureDigests"]) != set(worker.FIXTURES) or
            any(type(sha) is not str or not worker.SHA.fullmatch(sha) or
                sha == "0" * 64 for sha in data["fixtureDigests"].values())):
        raise SupervisorError("Privileged manifest could enable unsafe source")
    if (type(data["sessionId"]) is not str or
            type(data["tabId"]) is not str or
            not worker.GUID.fullmatch(data["sessionId"]) or
            not worker.GUID.fullmatch(data["tabId"]) or
            data["sessionId"] == data["tabId"]):
        raise SupervisorError("Ambiguous synthetic transaction identity")
    return data


def parse_manifest(raw: bytes) -> dict:
    if (type(raw) is not bytes or
            not 4 <= len(raw) <= MAX_MANIFEST_BYTES or
            raw.count(b"\n") != 1 or not raw.endswith(b"\n") or
            b"\r" in raw):
        raise SupervisorError("Invalid bounded root-owned manifest")
    try:
        value = json.loads(raw[:-1].decode("ascii", "strict"),
                           object_pairs_hook=_unique,
                           parse_constant=lambda _: (_ for _ in ()).throw(
                               SupervisorError("Nonfinite manifest constant")))
    except (ValueError, UnicodeError, RecursionError) as exc:
        if isinstance(exc, SupervisorError):
            raise
        raise SupervisorError("Invalid pinned root supervisor JSON") from exc
    return validate_manifest(value)


def build_inspect_packet(config: dict) -> bytes:
    cfg = validate_manifest(config)
    name = cfg["fixtureName"]
    packet = {
        "schema": worker.REQUEST_SCHEMA,
        "operation": "inspect_only",
        "fixtureName": name,
        "sourceSha256": cfg["fixtureDigests"][name],
        "expectedFinalName": worker.FIXTURES[name],
        "sessionId": cfg["sessionId"],
        "tabId": cfg["tabId"],
        "laboratoryOnly": True,
    }
    raw = (json.dumps(packet, separators=(",", ":")) + "\n").encode("ascii")
    try:
        worker.parse_request(raw)
    except worker.WorkerError as exc:
        raise SupervisorError("Synthetic worker request not valid") from exc
    return raw


def _root_directory(fd: int, *, exact_mode=None):
    meta = os.fstat(fd)
    if (not stat.S_ISDIR(meta.st_mode) or meta.st_uid != 0 or
            (meta.st_mode & 0o022) or
            (exact_mode is not None and
             (meta.st_mode & 0o777) != exact_mode)):
        raise SupervisorError("Untrusted root-owned path component")


def read_pinned_manifest() -> dict:
    """Read the fixed root-owned config; no env, CLI or user path arguments."""
    flags = os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK
    parent = -1
    fd = -1
    try:
        parent = os.open("/etc", flags | os.O_DIRECTORY)
        _root_directory(parent)
        second = os.open("getdoc-lab", flags | os.O_DIRECTORY, dir_fd=parent)
        os.close(parent)
        parent = second
        _root_directory(parent)
        fd = os.open("gd31j27t-root-supervisor.json", flags, dir_fd=parent)
        before = os.fstat(fd)
        if (not stat.S_ISREG(before.st_mode) or before.st_uid != 0 or
                (before.st_mode & 0o777) != 0o600 or
                before.st_nlink != 1 or
                not 4 <= before.st_size <= MAX_MANIFEST_BYTES):
            raise SupervisorError("Untrusted root-owned supervisor manifest inode")
        raw = os.read(fd, MAX_MANIFEST_BYTES + 1)
        after = os.fstat(fd)
        if (len(raw) != before.st_size or
                (before.st_dev, before.st_ino, before.st_size,
                 before.st_mtime_ns, before.st_ctime_ns) !=
                (after.st_dev, after.st_ino, after.st_size,
                 after.st_mtime_ns, after.st_ctime_ns)):
            raise SupervisorError("Pinned manifest inode changed while reading")
        return parse_manifest(raw)
    except OSError as exc:
        raise SupervisorError("Root-only manifest inaccessible") from exc
    finally:
        if fd >= 0:
            os.close(fd)
        if parent >= 0:
            os.close(parent)


def open_verified_handles(cfg: dict):
    """Open only fixed preexisting disposable tree; caller owns all 3 FDs.

    Root may traverse worker 0700 folders, but this function never creates,
    chowns, chmods, deletes or publishes anything.
    """
    cfg = validate_manifest(cfg)
    packet = worker.parse_request(build_inspect_packet(cfg))
    flags = os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK
    parents = []
    roles = []
    try:
        run = os.open("/run", flags | os.O_DIRECTORY)
        parents.append(run)
        _root_directory(run)
        base = os.open("getdoc-gd31j27t-disposable", flags | os.O_DIRECTORY,
                       dir_fd=run)
        parents.append(base)
        _root_directory(base, exact_mode=0o700)
        for name in (SCRATCH, OUTPUT):
            dfd = os.open(name, flags | os.O_DIRECTORY, dir_fd=base)
            roles.append(dfd)
        scratch, output = roles
        source = os.open(cfg["fixtureName"], flags, dir_fd=scratch)
        # Reorder from opened scratch/output/source to exact [src,scratch,out].
        roles = [source, scratch, output]
        worker.inspect_fds(roles, packet, worker_uid=cfg["workerUid"])
        return roles
    except (OSError, worker.WorkerError, ValueError) as exc:
        for fd in roles:
            try:
                os.close(fd)
            except OSError:
                pass
        if isinstance(exc, SupervisorError):
            raise
        raise SupervisorError("Synthetic root-owned FD preparation refused") from exc
    finally:
        for fd in parents:
            os.close(fd)


def denial():
    return {
        "schema": RESULT_SCHEMA,
        "state": "sent-unattested",
        "laboratoryOnly": True,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "releaseReady": False,
        "writePermissionGranted": False,
        "nativePublicationAuthorized": False,
        "trustedSupervisorAttested": False,
    }


class RootSendOnce:
    """One-shot supervisor-side sender; no retry after any failure."""
    def __init__(self):
        self.consumed = False

    def send(self, sock: socket.socket, config: dict, *, gate=None):
        if self.consumed:
            raise SupervisorError("Root supervisor send session already consumed")
        self.consumed = True
        cfg = validate_manifest(config)
        if (sys.platform != "linux" or gate != VM_TEST_ONLY_GATE or
                os.getuid() != 0 or os.geteuid() != 0 or
                sock.family != socket.AF_UNIX or
                sock.type & 0xF != socket.SOCK_SEQPACKET):
            raise SupervisorError("Unapproved offline root-only synthetic sender")
        try:
            raw = sock.getsockopt(
                socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
            peer_pid, peer_uid, peer_gid = struct.unpack("3i", raw)
        except (OSError, ValueError, struct.error) as exc:
            raise SupervisorError("Broker identity unavailable from kernel") from exc
        if (peer_pid != cfg["expectedBrokerPid"] or
                peer_uid != cfg["brokerUid"] or
                peer_gid != cfg["brokerGid"]):
            raise SupervisorError("Connected broker identity differs from pin")
        try:
            # Necessary root OS observation, NOT publication or launcher
            # attestation. An inaccessible /proc or excessive capabilities
            # must block FD opening instead of silently degrading checks.
            observed = audit.inspect_current_root()
            if (type(observed) is not dict or
                    observed.get("state") != "requires-independent-review" or
                    observed.get("kernelFactsObserved") is not True or
                    any(observed.get(key) is not False for key in (
                        "rootSandboxAttested", "seccompAllowlistAttested",
                        "mountRestrictionAttested", "nativePublicationAuthorized",
                        "launchAuthorized", "releaseReady",
                        "approvedForProduction", "writePermissionGranted"))):
                raise SupervisorError("Kernel root audit does not permit FD access")
            audit.require_broker_lifetime(
                broker_pid=peer_pid,
                expected_start_ticks=cfg["expectedBrokerStartTicks"])
        except audit.AuditError as exc:
            raise SupervisorError("Root or broker lifetime audit refused") from exc
        handles = []
        try:
            sock.settimeout(5.0)
            handles = open_verified_handles(cfg)
            packet = build_inspect_packet(cfg)
            rights = array.array("i", handles)
            size = sock.sendmsg(
                [packet], [(socket.SOL_SOCKET, socket.SCM_RIGHTS,
                            rights.tobytes())])
            if size != len(packet):
                raise SupervisorError("Ambiguous incomplete FD transfer; no retry")
            # After sendmsg the transfer may already have happened. If
            # process identity changes, refuse the receipt and NEVER retry.
            try:
                audit.require_broker_lifetime(
                    broker_pid=peer_pid,
                    expected_start_ticks=cfg["expectedBrokerStartTicks"])
            except audit.AuditError as exc:
                raise SupervisorError(
                    "Broker process disappeared after FD send; status unknown") from exc
            return denial()
        except (OSError, socket.timeout) as exc:
            raise SupervisorError("Synthetic FD transfer failed") from exc
        finally:
            for fd in handles:
                try:
                    os.close(fd)
                except OSError:
                    pass
