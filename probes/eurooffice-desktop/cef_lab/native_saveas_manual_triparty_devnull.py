#!/usr/bin/env python3
"""GD31J27ah: manually started, real three-PROCESS Unix IPC DENIAL smoke probe.

IMPORTANT: NOT a production launcher and NOT a real native SaveAs E2E.
This module NEVER starts another process, grants rights, creates users,
updates services, connects to networks or accesses any document. It can
only transfer three O_RDONLY /dev/null character-device FDs via SCM_RIGHTS.
All three processes MUST be independently launched by an operator in an
approved DISPOSABLE, OFFLINE Astra Linux VM with no corporate mounts.

Different pinned OS identities are mandatory: supervisor uid/gid 0/0,
broker uid=44001 gid=44003, worker uid=44002 gid=44003. UID creation,
runtime directories, signed binaries and independent VM attestation are
out of scope. Every role exits 2, even if denial was observed; no success
state exists. A manual gate/env flag is NOT evidence of VM isolation.
"""
from __future__ import annotations

import array
import fcntl
import os
import socket
import stat
import struct
import sys

BASE="/run/getdoc-gd31j27ah-disposable"
BROKER_DIR=BASE+"/broker"
WORKER_DIR=BASE+"/worker"
BROKER_SOCKET=BROKER_DIR+"/root.sock"
WORKER_SOCKET=WORKER_DIR+"/worker.sock"
VM_ENV="GETDOC_GD31J27AH_VM"
VM_ENV_VALUE="OFFLINE_DISPOSABLE_NO_DOCS"
MANUAL_ACK="I_APPROVE_DISPOSABLE_VM_NO_DOCUMENTS_DENIAL_ONLY"
BROKER_UID=44001
WORKER_UID=44002
IPC_GID=44003
ROOT_UID=0
TIMEOUT_SECONDS=30.0
REQUEST=b"GETDOC_GD31J27AH_THREE_DEVNULL_FDS_INSPECT_ONLY\n"
DENIED=(b'{"state":"denied","laboratoryOnly":true,'
        b'"nativePublicationAuthorized":false,"releaseReady":false,'
        b'"writePermissionGranted":false}\n')
DENIAL_STATE="denied"
ROLE_NAMES=("supervisor","broker","worker")
ROLE_ID={
    "supervisor":(ROOT_UID,0),
    "broker":(BROKER_UID,IPC_GID),
    "worker":(WORKER_UID,IPC_GID),
}
SOCKPATH={"broker":BROKER_SOCKET,"worker":WORKER_SOCKET}
DIRPATH={"broker":BROKER_DIR,"worker":WORKER_DIR}


class LabError(ValueError):
    """Do not expose filesystem, UID or peer details to stdout/stderr."""


def denial():
    return {
        "state":"denied","laboratoryOnly":True,
        "nativePublicationAuthorized":False,"writePermissionGranted":False,
        "releaseReady":False,"approvedForProduction":False,
        "transportAttested":False,"trustedVmIsolationAttested":False,
        "nativeSaveProven":False,
    }


def _preflight(role):
    """Only explicit, pre-provisioned Linux identities; never change UID."""
    if (role not in ROLE_NAMES or
            sys.platform!="linux" or
            os.environ.get(VM_ENV)!=VM_ENV_VALUE):
        raise LabError("Offline disposable OS gate missing")
    uid,gid=ROLE_ID[role]
    if (os.getuid()!=uid or os.geteuid()!=uid or
            os.getgid()!=gid or os.getegid()!=gid):
        raise LabError("Role requires independently provisioned OS identity")


def _check_parent(role):
    """Reject missing/replaced/unsafe externally provisioned runtime dirs."""
    if role not in ("broker","worker"):
        raise LabError("Unknown socket owner role")
    try:
        base=os.lstat(BASE)
        child=os.lstat(DIRPATH[role])
    except OSError as exc:
        raise LabError("Offline runtime directories unavailable") from exc
    if (not stat.S_ISDIR(base.st_mode) or
            base.st_uid!=0 or base.st_gid!=0 or
            stat.S_IMODE(base.st_mode)!=0o755 or
            not stat.S_ISDIR(child.st_mode) or
            child.st_uid!=ROLE_ID[role][0] or
            child.st_gid!=IPC_GID or
            stat.S_IMODE(child.st_mode)!=0o710):
        raise LabError("Untrusted private socket directory")


def _socket_inode(role):
    path=SOCKPATH[role]
    try:
        meta=os.lstat(path)
    except OSError as exc:
        raise LabError("Fixed Unix endpoint absent") from exc
    if (not stat.S_ISSOCK(meta.st_mode) or
            meta.st_uid!=ROLE_ID[role][0] or
            meta.st_gid!=IPC_GID or
            stat.S_IMODE(meta.st_mode)!=0o660):
        raise LabError("Fixed Unix endpoint owner/mode mismatch")
    return meta.st_dev,meta.st_ino


def _check_transport(sock):
    if (sock.family!=socket.AF_UNIX or
            sock.type & 0xF!=socket.SOCK_SEQPACKET):
        raise LabError("Only connected AF_UNIX SOCK_SEQPACKET is permitted")


def _peer_check(sock,expected_uid):
    _check_transport(sock)
    if type(expected_uid) is not int or expected_uid not in (0,BROKER_UID,WORKER_UID):
        raise LabError("Unknown expected Unix peer identity")
    try:
        buf=sock.getsockopt(socket.SOL_SOCKET,socket.SO_PEERCRED,
                            struct.calcsize("3i"))
        pid,uid,gid=struct.unpack("3i",buf)
    except (OSError,ValueError,struct.error) as exc:
        raise LabError("Actual connected peer credentials unavailable") from exc
    wanted_gid=0 if expected_uid==0 else IPC_GID
    if pid<=1 or uid!=expected_uid or gid!=wanted_gid:
        raise LabError("Kernel connected peer differs from fixed OS identity")
    return False


def _check_fd_roles(handles):
    """All three FDs are O_RDONLY /dev/null, never arbitrary documents."""
    if (type(handles) not in (tuple,list) or len(handles)!=3 or
            any(type(fd) is not int or fd<3 for fd in handles) or
            len(set(handles))!=3):
        raise LabError("Exactly three distinct synthetic FDs required")
    try:
        reference=os.stat("/dev/null",follow_symlinks=False)
        if not stat.S_ISCHR(reference.st_mode):
            raise LabError("Device is not a read-only synthetic character source")
        for fd in handles:
            data=os.fstat(fd)
            flags=fcntl.fcntl(fd,fcntl.F_GETFL)
            if (not stat.S_ISCHR(data.st_mode) or
                    data.st_rdev!=reference.st_rdev or
                    flags & os.O_ACCMODE!=os.O_RDONLY):
                raise LabError("FD is not the pinned read-only null device")
    except OSError as exc:
        raise LabError("Untrusted synthetic device file descriptor") from exc
    return tuple(handles)


def _extract_exact_rights(ancillary,flags):
    if flags & (socket.MSG_TRUNC|socket.MSG_CTRUNC):
        raise LabError("Truncated or extra Unix ancillary data")
    if (type(ancillary) is not list or len(ancillary)!=1 or
            ancillary[0][0]!=socket.SOL_SOCKET or
            ancillary[0][1]!=socket.SCM_RIGHTS or
            type(ancillary[0][2]) is not bytes):
        raise LabError("Unexpected Unix ancillary message")
    raw=ancillary[0][2]
    itemsize=array.array("i").itemsize
    if len(raw)!=3*itemsize:
        raise LabError("Unexpected SCM_RIGHTS descriptor count")
    vec=array.array("i")
    vec.frombytes(raw)
    return _check_fd_roles(vec.tolist())


def _extract_any_rights(ancillary):
    """Collect received rights for cleanup, even if packet is malformed."""
    found=[]
    for level,kind,raw in ancillary:
        if level==socket.SOL_SOCKET and kind==socket.SCM_RIGHTS:
            size=array.array("i").itemsize
            if len(raw)%size==0:
                a=array.array("i")
                a.frombytes(raw)
                found.extend(a.tolist())
    return found


def _recv_three(sock):
    _check_transport(sock)
    raw,anc,flags,_=sock.recvmsg(
        len(REQUEST)+1,socket.CMSG_SPACE(3*array.array("i").itemsize))
    received=_extract_any_rights(anc)
    try:
        if raw!=REQUEST:
            raise LabError("Unexpected synthetic Unix packet")
        _extract_exact_rights(anc,flags)
        return list(received)
    except (LabError,ValueError,TypeError):
        for fd in set(received):
            if type(fd) is int and fd>=3:
                try:
                    os.close(fd)
                except OSError:
                    pass
        raise


def _send_exact_three(sock,handles):
    _check_transport(sock)
    fds=_check_fd_roles(handles)
    rights=array.array("i",fds)
    n=sock.sendmsg(
        [REQUEST],
        [(socket.SOL_SOCKET,socket.SCM_RIGHTS,rights.tobytes())])
    if n!=len(REQUEST):
        raise LabError("Incomplete one-shot synthetic FD transfer; no retry")


def _require_denial(raw):
    if type(raw) is not bytes or raw!=DENIED:
        raise LabError("Unknown or authorizing response prohibited")
    return False


def _owned_listener(role):
    _check_parent(role)
    # Never unlink an old endpoint; requiring an absent path prevents replay.
    if os.path.lexists(SOCKPATH[role]):
        raise LabError("Stale or pre-existing Unix endpoint refuses session")
    sock=socket.socket(socket.AF_UNIX,
                       socket.SOCK_SEQPACKET|socket.SOCK_CLOEXEC)
    try:
        if sock.fileno()!=3:
            raise LabError("Unexpected inherited process file descriptor")
        old=os.umask(0o117)  # externally provisioned gid enables 0660 sockets
        try:
            sock.bind(SOCKPATH[role])
        finally:
            os.umask(old)
        _check_parent(role)
        _socket_inode(role)
        sock.listen(1)
        sock.settimeout(TIMEOUT_SECONDS)
        return sock
    except (OSError,LabError,ValueError):
        sock.close()
        # Intentionally no unlink: operator must dispose of entire VM.
        raise


def _connect_fixed(role):
    _check_parent(role)
    before=_socket_inode(role)
    sock=socket.socket(socket.AF_UNIX,
                       socket.SOCK_SEQPACKET|socket.SOCK_CLOEXEC)
    try:
        sock.settimeout(TIMEOUT_SECONDS)
        sock.connect(SOCKPATH[role])
        if _socket_inode(role)!=before:
            raise LabError("Unix endpoint inode changed during connection")
        _peer_check(sock,ROLE_ID[role][0])
        return sock
    except (OSError,LabError,ValueError):
        sock.close()
        raise


def _open_only_nulls():
    flags=os.O_RDONLY|os.O_CLOEXEC|os.O_NOFOLLOW|os.O_NONBLOCK
    opened=[]
    try:
        for _ in range(3):
            opened.append(os.open("/dev/null",flags))
        _check_fd_roles(opened)
        return opened
    except (OSError,LabError):
        for fd in reversed(opened):
            os.close(fd)
        raise


def _supervisor_once():
    with _connect_fixed("broker") as peer:
        _peer_check(peer,BROKER_UID)
        handles=_open_only_nulls()
        try:
            _send_exact_three(peer,handles)
        finally:
            for fd in reversed(handles):
                os.close(fd)
        _require_denial(peer.recv(len(DENIED)+1))


def _broker_once():
    with _owned_listener("broker") as listener:
        channel,_=listener.accept()
        with channel:
            channel.settimeout(TIMEOUT_SECONDS)
            _peer_check(channel,ROOT_UID)
            received=_recv_three(channel)
            try:
                with _connect_fixed("worker") as peer:
                    _peer_check(peer,WORKER_UID)
                    _send_exact_three(peer,received)
                    _require_denial(peer.recv(len(DENIED)+1))
                channel.sendall(DENIED)
            finally:
                for fd in reversed(received):
                    os.close(fd)


def _worker_once():
    with _owned_listener("worker") as listener:
        channel,_=listener.accept()
        with channel:
            channel.settimeout(TIMEOUT_SECONDS)
            _peer_check(channel,BROKER_UID)
            received=_recv_three(channel)
            try:
                _check_fd_roles(received)
                channel.sendall(DENIED)
            finally:
                for fd in reversed(received):
                    os.close(fd)


def main(argv=None):
    """Explicit operator invocation; code NEVER launches other roles."""
    args=sys.argv[1:] if argv is None else argv
    if (type(args) not in (tuple,list) or len(args)!=2 or
            args[0] not in ROLE_NAMES or args[1]!=MANUAL_ACK):
        return 2
    role=args[0]
    try:
        _preflight(role)
        if role=="supervisor":
            _supervisor_once()
            # This marker is only a local operator-facing diagnostic. It is
            # NOT proof of origin, VM isolation, durable save or attestation.
            print("GD31J27AH_DENIAL_OBSERVED_NOT_ATTESTED")
        elif role=="broker":
            _broker_once()
        elif role=="worker":
            _worker_once()
    except (LabError,OSError,ValueError,struct.error,socket.timeout):
        pass
    return 2  # ALWAYS DENIED, even when real IPC was observed


if __name__=="__main__":
    raise SystemExit(main())
