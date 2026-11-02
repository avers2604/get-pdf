#!/usr/bin/env python3
"""GD31J27y: explicit-only, rights-free FD3 challenge / response LAB.

A broker sends one unpredictable nonce on the already connected AF_UNIX
SOCK_SEQPACKET channel; worker must receive it ON fd3 and return the nonce
with its fstat(3).st_ino. Broker checks the inode against bounded kernel
/proc/<worker pid>/fd/3 readlinks both before and after the challenge.

This is REPLAY RESISTANCE and read-only diagnostic evidence, NOT verified
worker code, independent launcher provenance, kernel socket-pair attestation,
trusted cgroup, Astra MAC or permission to write. A compromised worker can
forge application-level replies. Neither this module nor its caller publishes
files, transfers SCM_RIGHTS, changes UID or creates a process/socket.

Both recvmsg calls set ancillary buffer size 0 and MUST reject MSG_CTRUNC:
per Linux unix(7), surplus SCM_RIGHTS FDs are discarded and closed by kernel.
The regular worker main() and broker listener main() DO NOT call this module.
"""
from __future__ import annotations

import os
import re
import socket
import stat
import struct
import sys

CHALLENGE_PREFIX=b"GETDOC-GD31J27Y-CHAL/1:"
ACK_PREFIX=b"GETDOC-GD31J27Y-ACK/1:"
NONCE_BYTES=32
INODE_FMT="!Q"
MAX_INODE=(1<<64)-1
TARGET=re.compile(r"socket:\[([1-9][0-9]{0,19})\]\Z",re.ASCII)
RESULT_SCHEMA="getdoc-gd31j27y-fd3-nonce-observation-deny-only/1"


class Fd3ChallengeError(ValueError):
    """Sanitized error; never include socket paths, process FDs or secrets."""


def denial(*, observed=False):
    return {
        "schema":RESULT_SCHEMA, "state":"requires-independent-review",
        "workerFd3HandshakeObserved":observed,
        "fd3PairedAttested":False,
        "workerExecutableAttested":False,
        "workerLaunchAttested":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "nativeSaveProven":False,
        "documentVerified":False,
        "saveVerified":False,
        "releaseReady":False,
        "approvedForProduction":False,
        "laboratoryOnly":True,
    }


def _validate_channel(sock):
    if (not sys.platform.startswith("linux") or
            sock.family!=socket.AF_UNIX or
            sock.type & 0xF!=socket.SOCK_SEQPACKET or
            type(sock.fileno()) is not int or sock.fileno()<0):
        raise Fd3ChallengeError("Unverified Unix seqpacket channel")


def _creds(sock):
    try:
        raw=sock.getsockopt(socket.SOL_SOCKET,
                            socket.SO_PEERCRED,struct.calcsize("3i"))
        if type(raw) is not bytes or len(raw)!=struct.calcsize("3i"):
            raise Fd3ChallengeError("Malformed kernel socket credentials")
        return struct.unpack("3i",raw)
    except (OSError,ValueError,struct.error) as exc:
        if isinstance(exc,Fd3ChallengeError):
            raise
        raise Fd3ChallengeError("Kernel socket identity unavailable") from exc


def _packet(sock,size):
    if not hasattr(socket,"MSG_CMSG_CLOEXEC"):
        raise Fd3ChallengeError("Cannot prevent inherited SCM_RIGHTS")
    try:
        packet, ancillary, flags, _=sock.recvmsg(
            size+1,0,socket.MSG_CMSG_CLOEXEC)
    except (OSError,socket.timeout) as exc:
        raise Fd3ChallengeError("One-shot FD3 challenge response unavailable") from exc
    # ancbufsize zero: any SCM_RIGHTS must be dropped & closed by Linux.
    # Never accept an unsolicited payload carrying descriptors or creds.
    if (len(packet)!=size or ancillary or
            flags & (socket.MSG_TRUNC|socket.MSG_CTRUNC)):
        raise Fd3ChallengeError("Unexpected challenge size or ancillary rights")
    return packet


def _fd3_inode(pid):
    if type(pid) is not int or not 1<=pid<2**31:
        raise Fd3ChallengeError("Unpinned worker process identity")
    try:
        target=os.readlink(f"/proc/{pid}/fd/3")
    except OSError as exc:
        raise Fd3ChallengeError("Worker FD3 kernel inode inaccessible") from exc
    found=TARGET.fullmatch(target)
    if not found:
        raise Fd3ChallengeError("Worker FD3 is not a kernel socket inode")
    number=int(found.group(1))
    if number>MAX_INODE:
        raise Fd3ChallengeError("Worker socket inode exceeds wire format")
    return number


def respond_fd3_once(sock,*,expected_broker_pid,expected_broker_uid):
    """Unwired worker callable, NEVER invoked by worker.main/serve_one."""
    _validate_channel(sock)
    if (sock.fileno()!=3 or
            type(expected_broker_pid) is not int or
            not 1<=expected_broker_pid<2**31 or
            type(expected_broker_uid) is not int or
            expected_broker_uid<=0):
        raise Fd3ChallengeError("Worker proof requires exact FD3 and pinned broker")
    pid, uid, gid=_creds(sock)
    if (pid!=expected_broker_pid or uid!=expected_broker_uid or
            uid==os.geteuid() or gid<=0):
        raise Fd3ChallengeError("Worker kernel broker credentials differ from pin")
    try:
        initial=os.fstat(3)
        channel=os.fstat(sock.fileno())
    except OSError as exc:
        raise Fd3ChallengeError("Worker kernel FD3 unavailable") from exc
    if (not stat.S_ISSOCK(initial.st_mode) or
            initial.st_ino<=0 or initial.st_ino>MAX_INODE or
            (initial.st_dev,initial.st_ino)!=(channel.st_dev,channel.st_ino)):
        raise Fd3ChallengeError("Worker FD3 does not match response socket")
    sock.settimeout(5.0)
    incoming=_packet(sock,len(CHALLENGE_PREFIX)+NONCE_BYTES)
    if (not incoming.startswith(CHALLENGE_PREFIX) or
            incoming[len(CHALLENGE_PREFIX):]==b"\x00"*NONCE_BYTES):
        raise Fd3ChallengeError("Bad broker FD3 challenge")
    # Reject an FD3 switch immediately before reply.
    try:
        second=os.fstat(3)
    except OSError as exc:
        raise Fd3ChallengeError("Worker FD3 changed during challenge") from exc
    if (second.st_dev,second.st_ino)!=(initial.st_dev,initial.st_ino):
        raise Fd3ChallengeError("Worker FD3 replaced during nonce verification")
    sock.sendall(
        ACK_PREFIX+incoming[len(CHALLENGE_PREFIX):]+
        struct.pack(INODE_FMT,initial.st_ino))
    return denial(observed=True)


def challenge_worker_fd3_once(sock,*,expected_worker_pid,expected_worker_uid,
                              broker_uid):
    """Unwired broker preflight. No SCM_RIGHTS until separate security review.

    A successful application echo cannot establish trusted executable origin
    and MUST NOT set fd3PairedAttested, launchAuthorized or releaseReady.
    """
    _validate_channel(sock)
    if (type(expected_worker_pid) is not int or
            not 1<=expected_worker_pid<2**31 or
            type(expected_worker_uid) is not int or expected_worker_uid<=0 or
            type(broker_uid) is not int or broker_uid<=0 or
            broker_uid==expected_worker_uid or broker_uid!=os.geteuid()):
        raise Fd3ChallengeError("Unpinned distinct worker/broker identities")
    pid, uid, gid=_creds(sock)
    if pid!=expected_worker_pid or uid!=expected_worker_uid or gid<=0:
        raise Fd3ChallengeError("Broker Unix socket peer does not match worker")
    before=_fd3_inode(pid)
    try:
        nonce=os.urandom(NONCE_BYTES)
    except OSError as exc:
        raise Fd3ChallengeError("Kernel entropy unavailable") from exc
    if type(nonce) is not bytes or len(nonce)!=NONCE_BYTES or nonce==b"\x00"*NONCE_BYTES:
        raise Fd3ChallengeError("Invalid FD3 proof nonce")
    sock.settimeout(5.0)
    # One challenge request, one response, NO FD capabilities.
    try:
        sock.sendall(CHALLENGE_PREFIX+nonce)
    except (OSError,socket.timeout) as exc:
        raise Fd3ChallengeError("FD3 challenge send failed; no retry") from exc
    reply=_packet(sock,len(ACK_PREFIX)+NONCE_BYTES+struct.calcsize(INODE_FMT))
    if (not reply.startswith(ACK_PREFIX) or
            reply[len(ACK_PREFIX):len(ACK_PREFIX)+NONCE_BYTES]!=nonce):
        raise Fd3ChallengeError("FD3 response did not echo one-time challenge")
    inode=struct.unpack(INODE_FMT,reply[-struct.calcsize(INODE_FMT):])[0]
    if inode!=before or _fd3_inode(pid)!=inode:
        raise Fd3ChallengeError("Worker FD3 kernel inode replaced or mismatched")
    return denial(observed=True)
