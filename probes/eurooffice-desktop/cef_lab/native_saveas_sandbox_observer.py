#!/usr/bin/env python3
"""GD31J27n: read-only Linux /proc observations for DENY-ONLY native worker.

Observed kernel bits are necessary, never sufficient. No process spawning,
namespace manipulation, privilege changes, filesystem output or network.
This observer cannot attest seccomp BPF rules, private mount contents,
credentials of a launcher, absent corporate data or final SaveAs durability.
"""
from __future__ import annotations

import os
import re
import sys

SCHEMA = "getdoc-gd31j27n-observed-kernel-sandbox-denial-lab/1"
MAX_STATUS = 32768
MAX_MOUNTINFO = 256 * 1024
MAX_MOUNTS = 512
REQUIRED_STATUS = frozenset({
    "Uid", "Gid", "NoNewPrivs", "Seccomp", "Seccomp_filters",
    "CapInh", "CapPrm", "CapEff", "CapBnd", "CapAmb", "TracerPid",
})
CAP_ZERO = re.compile(r"0+\Z", re.ASCII)
REMOTE_FS = frozenset({
    "cifs", "smb3", "nfs", "nfs4", "9p", "davfs", "ceph", "glusterfs",
    "lustre", "afs", "coda", "fuse.sshfs", "fuse.rclone",
    "fuse.gvfsd-fuse",
})


class ObservationError(ValueError):
    """Refusal messages never contain paths or sensitive status values."""


def parse_status(text: str) -> dict:
    if type(text) is not str or not 20 <= len(text) <= MAX_STATUS:
        raise ObservationError("Unapproved kernel process status")
    found = {}
    for line in text.splitlines():
        key, separator, value = line.partition(":")
        if separator and key in REQUIRED_STATUS:
            if key in found or not value.strip():
                raise ObservationError("Ambiguous kernel security status")
            found[key] = value.strip()
    if set(found) != REQUIRED_STATUS:
        raise ObservationError("Missing mandatory kernel security fields")
    return found


def parse_mountinfo(text: str) -> int:
    if type(text) is not str or not 1 <= len(text) <= MAX_MOUNTINFO:
        raise ObservationError("Missing or oversized mount namespace evidence")
    lines = text.splitlines()
    if not 1 <= len(lines) <= MAX_MOUNTS:
        raise ObservationError("Unbounded or empty mount namespace")
    for line in lines:
        if line.count(" - ") != 1:
            raise ObservationError("Unparseable mount namespace entry")
        before, after = line.split(" - ", 1)
        meta, details = before.split(), after.split()
        if (len(meta) < 6 or len(details) < 3 or
                not meta[0].isdigit() or not meta[1].isdigit()):
            raise ObservationError("Invalid mount namespace entry")
        kind = details[0].lower()
        if kind in REMOTE_FS or kind.startswith("fuse."):
            raise ObservationError("External filesystem detected in worker mount namespace")
    return len(lines)


def inspect_observed(*, status_text: str, mountinfo_text: str,
                     own_ns: dict, peer_ns: dict,
                     expected_uid: int, peer_uid: int) -> dict:
    if (type(expected_uid) is not int or expected_uid <= 0 or
            type(peer_uid) is not int or peer_uid < 0 or
            peer_uid == expected_uid or
            type(own_ns) is not dict or type(peer_ns) is not dict or
            set(own_ns) != {"mnt", "net"} or set(peer_ns) != {"mnt", "net"} or
            any(type(n) is not int or n <= 0 for n in
                (*own_ns.values(), *peer_ns.values())) or
            own_ns["mnt"] == peer_ns["mnt"] or
            own_ns["net"] == peer_ns["net"]):
        raise ObservationError("Unapproved worker identity/namespace separation")

    status = parse_status(status_text)
    for key in ("Uid", "Gid"):
        values = status[key].split()
        if len(values) != 4 or any(not item.isdecimal() for item in values):
            raise ObservationError("Unstable process identity")
        if key == "Uid" and any(int(value) != expected_uid for value in values):
            raise ObservationError("Worker UID differs from pinned expected UID")
        if key == "Gid" and (int(values[0]) <= 0 or len(set(values)) != 1):
            raise ObservationError("Unstable or privileged worker GID")

    if (status["NoNewPrivs"] != "1" or status["Seccomp"] != "2" or
            not status["Seccomp_filters"].isdecimal() or
            not 1 <= int(status["Seccomp_filters"]) <= 32 or
            status["TracerPid"] != "0" or
            any(not CAP_ZERO.fullmatch(status[key]) for key in
                ("CapInh", "CapPrm", "CapEff", "CapBnd", "CapAmb"))):
        raise ObservationError("Linux privilege or seccomp prerequisites not met")

    count = parse_mountinfo(mountinfo_text)
    return {
        "schema": SCHEMA,
        "reviewState": "requires-manual-review",
        "mountEntriesObserved": count,
        "kernelFactsObserved": True,
        "corporateMountsAbsentAttested": False,
        "seccompFilterSemanticsAttested": False,
        "inheritedFdIsolationAttested": False,
        "kernelIsolationAttested": False,
        "brokerLaunchAuthorized": False,
        "nativePublicationAuthorized": False,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "releaseReady": False,
        "approvedForProduction": False,
        "writePermissionGranted": False,
        "laboratoryOnly": True,
    }


def _limited_read(path: str, max_length: int) -> str:
    # Fixed /proc path constructed from a kernel-attested peer PID, no
    # user-supplied document paths. O_NOFOLLOW prevents symlink substitution.
    fd = -1
    try:
        fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        with os.fdopen(fd, "rb") as stream:
            fd = -1
            raw = stream.read(max_length + 1)
        if len(raw) > max_length:
            raise ObservationError("Kernel evidence is too large")
        return raw.decode("ascii", "strict")
    except (OSError, UnicodeError) as exc:
        raise ObservationError("Kernel observation is inaccessible") from exc
    finally:
        if fd >= 0:
            os.close(fd)


def inspect_current(*, peer_pid: int, peer_uid: int) -> dict:
    """Observe only the running worker and the peer PID from SO_PEERCRED.

    Never treat caller-supplied PID/UID or these facts as independent
    sandbox attestation. The actual connected Unix socket is the authority
    for peer credentials and the broker's identity.
    """
    if (not sys.platform.startswith("linux") or
            type(peer_pid) is not int or peer_pid <= 0 or
            type(peer_uid) is not int or peer_uid < 0):
        raise ObservationError("Unsupported kernel observer input")
    try:
        mine = {k: os.stat(f"/proc/self/ns/{k}").st_ino
                for k in ("mnt", "net")}
        peer = {k: os.stat(f"/proc/{peer_pid}/ns/{k}").st_ino
                for k in ("mnt", "net")}
    except OSError as exc:
        raise ObservationError("Kernel namespace identity unavailable") from exc
    return inspect_observed(
        status_text=_limited_read("/proc/self/status", MAX_STATUS),
        mountinfo_text=_limited_read("/proc/self/mountinfo", MAX_MOUNTINFO),
        own_ns=mine, peer_ns=peer,
        expected_uid=os.geteuid(), peer_uid=peer_uid,
    )
