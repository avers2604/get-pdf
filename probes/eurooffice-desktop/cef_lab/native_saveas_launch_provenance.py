#!/usr/bin/env python3
"""GD31J27z: read-only root-pinned launch facts for broker/worker, DENY ONLY.

Trust starts from an INDEPENDENT root-owned fixed manifest, never from
worker JSON, CEF, a request packet or cgroup self-report. Snapshot checks
are necessary observations and NEVER certify executable provenance.

The inspector checks: pinned PID and birth ticks via a held kernel pidfd;
exact four real/effective/saved/filesystem UID/GID values; exact cgroup-v2
path; exact null-delimited process argv; /proc/PID/exe readlink and SHA256
of the OPEN executable inode; SHA256 of a fixed root-owned .py source.

This does NOT pin/import-audit every Python dependency, prove a specific
systemd unit started the process, verify a signed image, attest Astra MAC or
BPF, guarantee no corporate mounts, or authorize a SaveAs. Hidden cross-UID
/proc is fail-closed, never an excuse to grant CAP_SYS_PTRACE.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import stat
import sys

import native_saveas_root_supervisor_audit as audit
import native_saveas_pidfd_provenance as pidfd

MANIFEST_SCHEMA="getdoc-gd31j27z-root-pinned-process-launch-lab/1"
REPORT_SCHEMA="getdoc-gd31j27z-process-provenance-review-only/1"
FIXED_MANIFEST="/etc/getdoc-lab/gd31j27z-launch-pins.json"
SCRIPT_PATHS={
    "broker":"/opt/getdoc-lab/bin/native_saveas_broker_listener.py",
    "worker":"/opt/getdoc-lab/bin/native_saveas_fd_worker.py",
}
CGROUP_PATHS={
    "broker":"/system.slice/getdoc-gd31j27z-broker.service",
    "worker":"/system.slice/getdoc-gd31j27z-worker.service",
}
MAX_MANIFEST=4096
MAX_PROC_TEXT=8192
MAX_EXECUTABLE_BYTES=64*1024*1024
SHA=re.compile(r"[0-9a-f]{64}\Z",re.ASCII)
ROLE_FIELDS={"pid","uid","gid","startTicks","cgroup","exeTarget",
             "exeSha256","scriptSha256","argv"}
TOP_FIELDS={"schema","laboratoryOnly","publishEnabled","mode","profiles"}

class ProvenanceError(ValueError):
    """No user source paths, file contents, command line or secrets in errors."""


def _unique_pairs(pairs):
    values={}
    for k,v in pairs:
        if k in values:
            raise ProvenanceError("Duplicate root manifest key")
        values[k]=v
    return values


def _valid_profile(role,info):
    if type(info) is not dict or set(info)!=ROLE_FIELDS:
        raise ProvenanceError("Unexpected process launch pin schema")
    if (type(info["pid"]) is not int or not 1<=info["pid"]<2**31 or
            type(info["uid"]) is not int or info["uid"]<=0 or
            type(info["gid"]) is not int or info["gid"]<=0 or
            type(info["startTicks"]) is not int or info["startTicks"]<=0 or
            info["cgroup"]!=CGROUP_PATHS[role] or
            type(info["exeTarget"]) is not str or
            not re.fullmatch(r"/usr/bin/python3\.[0-9]{1,2}",info["exeTarget"]) or
            any(type(info[key]) is not str or not SHA.fullmatch(info[key]) or
                info[key]=="0"*64 for key in ("exeSha256","scriptSha256")) or
            type(info["argv"]) is not list or
            info["argv"]!=["/usr/bin/python3","-I","-S",SCRIPT_PATHS[role]]):
        raise ProvenanceError("Untrusted process lifetime, executable or cgroup pin")
    return info


def validate_manifest(data):
    if (type(data) is not dict or set(data)!=TOP_FIELDS or
            data["schema"]!=MANIFEST_SCHEMA or
            data["laboratoryOnly"] is not True or
            data["publishEnabled"] is not False or
            data["mode"]!="inspect_only" or
            type(data["profiles"]) is not dict or
            set(data["profiles"])!=set(SCRIPT_PATHS)):
        raise ProvenanceError("Untrusted root launch provenance policy")
    broker=_valid_profile("broker",data["profiles"]["broker"])
    worker=_valid_profile("worker",data["profiles"]["worker"])
    if broker["pid"]==worker["pid"] or broker["uid"]==worker["uid"]:
        raise ProvenanceError("Broker and worker process identities overlap")
    return data


def parse_pinned_manifest(raw):
    if (type(raw) is not bytes or len(raw)<4 or len(raw)>MAX_MANIFEST or
            not raw.endswith(b"\n") or raw.count(b"\n")!=1 or b"\r" in raw):
        raise ProvenanceError("Root launch manifest is not one bounded JSON line")
    try:
        data=json.loads(raw[:-1].decode("ascii"),
                        object_pairs_hook=_unique_pairs,
                        parse_constant=lambda _: (_ for _ in ()).throw(
                            ProvenanceError("Nonfinite process manifest constant")))
    except (UnicodeError,ValueError,RecursionError) as exc:
        if isinstance(exc,ProvenanceError):
            raise
        raise ProvenanceError("Root manifest is not valid strict JSON") from exc
    return validate_manifest(data)


def _secure_root_directory(fd):
    info=os.fstat(fd)
    if (not stat.S_ISDIR(info.st_mode) or info.st_uid!=0 or
            info.st_mode & 0o022):
        raise ProvenanceError("Untrusted root-owned manifest directory")


def _root_regular(fd, *, exact_mode=None):
    info=os.fstat(fd)
    if (not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or
            info.st_nlink!=1 or info.st_mode & 0o022 or
            (exact_mode is not None and
             info.st_mode & 0o777!=exact_mode)):
        raise ProvenanceError("Source or interpreter inode not independently trusted")
    return info


def _hash_bounded_fd(fd):
    before=_root_regular(fd)
    if not 0<before.st_size<=MAX_EXECUTABLE_BYTES:
        raise ProvenanceError("Executable/source size exceeds synthetic policy")
    h=hashlib.sha256()
    read=0
    while read<before.st_size:
        chunk=os.read(fd,min(65536,before.st_size-read))
        if not chunk:
            raise ProvenanceError("Executable/source bytes truncated")
        h.update(chunk)
        read+=len(chunk)
    after=os.fstat(fd)
    identity=("st_dev","st_ino","st_size","st_mtime_ns","st_ctime_ns")
    if tuple(getattr(before,k) for k in identity)!=tuple(
            getattr(after,k) for k in identity):
        raise ProvenanceError("Pinned executable/source inode changed while reading")
    return h.hexdigest()


def load_root_owned_manifest():
    """Only fixed /etc/getdoc-lab/gd31j27z-launch-pins.json, root mode0600."""
    if not sys.platform.startswith("linux"):
        raise ProvenanceError("Linux-only root manifest")
    flags=os.O_RDONLY|os.O_CLOEXEC|os.O_NOFOLLOW|os.O_NONBLOCK
    opened=[]
    try:
        etc=os.open("/etc",flags|os.O_DIRECTORY)
        opened.append(etc);_secure_root_directory(etc)
        parent=os.open("getdoc-lab",flags|os.O_DIRECTORY,dir_fd=etc)
        opened.append(parent);_secure_root_directory(parent)
        fd=os.open("gd31j27z-launch-pins.json",flags,dir_fd=parent)
        opened.append(fd)
        before=_root_regular(fd,exact_mode=0o600)
        if not 4<=before.st_size<=MAX_MANIFEST:
            raise ProvenanceError("Oversized root-owned launch manifest")
        raw=os.read(fd,MAX_MANIFEST+1)
        after=os.fstat(fd)
        if (len(raw)!=before.st_size or
                (before.st_dev,before.st_ino,before.st_mtime_ns,
                 before.st_ctime_ns,before.st_size)!=
                (after.st_dev,after.st_ino,after.st_mtime_ns,
                 after.st_ctime_ns,after.st_size)):
            raise ProvenanceError("Root launch manifest changed during inspection")
        return parse_pinned_manifest(raw)
    except OSError as exc:
        raise ProvenanceError("Fixed root launch manifest inaccessible") from exc
    finally:
        for fd in reversed(opened):
            os.close(fd)


def _read_proc(pid, key):
    if key not in {"cmdline","cgroup","status"}:
        raise ProvenanceError("Unexpected procfs observation name")
    fd=-1
    try:
        fd=os.open(f"/proc/{pid}/{key}",
                   os.O_RDONLY|os.O_CLOEXEC|os.O_NONBLOCK|os.O_NOFOLLOW)
        value=os.read(fd,MAX_PROC_TEXT+1)
    except OSError as exc:
        raise ProvenanceError("Cross-UID procfs not readable") from exc
    finally:
        if fd>=0:
            os.close(fd)
    if not value or len(value)>MAX_PROC_TEXT:
        raise ProvenanceError("Process kernel data missing or truncated")
    return value


def _hash_executable(pid,expected_target):
    fd=-1
    try:
        link=os.readlink(f"/proc/{pid}/exe")
        if link!=expected_target:
            raise ProvenanceError("Kernel executable target differs from trusted pin")
        # Deliberately dereference the kernel-generated /proc/pid/exe link.
        # This opens the ACTUAL executed binary inode, not an argv path.
        fd=os.open(f"/proc/{pid}/exe",
                   os.O_RDONLY|os.O_CLOEXEC|os.O_NONBLOCK)
        digest=_hash_bounded_fd(fd)
    except OSError as exc:
        raise ProvenanceError("Executed interpreter inaccessible under procfs") from exc
    finally:
        if fd>=0:
            os.close(fd)
    return digest


def _hash_fixed_source(role):
    filename=SCRIPT_PATHS[role].rsplit("/",1)[1]
    flags=os.O_RDONLY|os.O_CLOEXEC|os.O_NOFOLLOW|os.O_NONBLOCK
    opened=[]
    try:
        opt=os.open("/opt",flags|os.O_DIRECTORY)
        opened.append(opt);_secure_root_directory(opt)
        lab=os.open("getdoc-lab",flags|os.O_DIRECTORY,dir_fd=opt)
        opened.append(lab);_secure_root_directory(lab)
        directory=os.open("bin",flags|os.O_DIRECTORY,dir_fd=lab)
        opened.append(directory);_secure_root_directory(directory)
        fd=os.open(filename,flags,dir_fd=directory)
        opened.append(fd)
        return _hash_bounded_fd(fd)
    except OSError as exc:
        raise ProvenanceError("Fixed root-owned source inaccessible") from exc
    finally:
        for fd in reversed(opened):
            os.close(fd)


def _process_status_owners(value,expected_uid,expected_gid):
    try:
        lines=value.decode("ascii").splitlines()
    except UnicodeError as exc:
        raise ProvenanceError("Invalid kernel status owner encoding") from exc
    owners={}
    for line in lines:
        key,colon,field=line.partition(":")
        if key in ("Uid","Gid"):
            if key in owners or colon!=":":
                raise ProvenanceError("Duplicate process identity kernel field")
            parts=field.split()
            if len(parts)!=4 or any(not p.isdecimal() for p in parts):
                raise ProvenanceError("Malformed process identity")
            owners[key]=tuple(int(x) for x in parts)
    if (set(owners)!={"Uid","Gid"} or
            owners["Uid"]!=(expected_uid,)*4 or
            owners["Gid"]!=(expected_gid,)*4):
        raise ProvenanceError("Process UID/GID do not match pinned launch")


def denial():
    return {
        "schema":REPORT_SCHEMA,"state":"requires-independent-review",
        "kernelLaunchFactsObserved":False,
        "workerExecutableAttested":False,
        "brokerExecutableAttested":False,
        "trustedRootManifestAttested":False,
        "cgroupAttested":False,
        "mountIsolationAttested":False,
        "seccompAllowlistAttested":False,
        "launchAuthorized":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "releaseReady":False,
        "approvedForProduction":False,
        "laboratoryOnly":True,
    }


def inspect_snapshot(profile,snapshot):
    if (type(profile) is not dict or
            type(snapshot) is not dict or
            set(snapshot)!={"cmdline","cgroup","status",
                            "exeTarget","exeSha256","scriptSha256"}):
        raise ProvenanceError("Kernel launcher snapshot schema mismatch")
    if (type(snapshot["cmdline"]) is not bytes or
            type(snapshot["cgroup"]) is not str or
            type(snapshot["status"]) is not str or
            type(snapshot["exeTarget"]) is not str or
            type(snapshot["exeSha256"]) is not str or
            type(snapshot["scriptSha256"]) is not str):
        raise ProvenanceError("Invalid kernel launcher snapshot types")
    argv=(b"\x00".join(x.encode("ascii") for x in profile["argv"])+b"\x00")
    if snapshot["cmdline"]!=argv:
        raise ProvenanceError("Live process arguments differ from pinned launcher")
    if snapshot["cgroup"]!="0::"+profile["cgroup"]+"\n":
        raise ProvenanceError("Process is outside pinned cgroup v2 service")
    _process_status_owners(snapshot["status"],
                           profile["uid"],profile["gid"])
    if (snapshot["exeTarget"]!=profile["exeTarget"] or
            snapshot["exeSha256"]!=profile["exeSha256"] or
            snapshot["scriptSha256"]!=profile["scriptSha256"]):
        raise ProvenanceError("Executed interpreter/source digest differs from pin")
    return {**denial(),"kernelLaunchFactsObserved":True}


def inspect_live_process(profile):
    """Pin one process with pidfd during all read-only kernel observations."""
    if type(profile) is not dict or set(profile)!=ROLE_FIELDS:
        raise ProvenanceError("Process pin fields not complete")
    role=next((key for key,expected in CGROUP_PATHS.items()
               if profile.get("cgroup")==expected),None)
    if role is None:
        raise ProvenanceError("Process cgroup role is not independently pinned")
    _valid_profile(role,profile)
    pid=profile["pid"]
    guard=None
    try:
        guard=pidfd.pin_broker_process(pid,profile["startTicks"])
        guard.assert_live()
        before=audit.read_broker_start_ticks(pid)
        if before!=profile["startTicks"]:
            raise ProvenanceError("Process birth time changed before executable check")
        snapshot={
            "cgroup":_read_proc(pid,"cgroup").decode("ascii"),
            "cmdline":_read_proc(pid,"cmdline"),
            "status":_read_proc(pid,"status").decode("ascii"),
            "exeTarget":os.readlink(f"/proc/{pid}/exe"),
            "exeSha256":_hash_executable(pid,profile["exeTarget"]),
            "scriptSha256":_hash_fixed_source(role),
        }
        report=inspect_snapshot(profile,snapshot)
        # Re-observe the kernel command line, cgroup, owner identifiers and
        # executable link AFTER digesting both files. PID birthtime alone
        # cannot detect execve or cgroup changes in an existing process.
        if (snapshot["cmdline"]!=_read_proc(pid,"cmdline") or
                snapshot["cgroup"].encode("ascii")!=_read_proc(pid,"cgroup") or
                snapshot["status"].encode("ascii")!=_read_proc(pid,"status") or
                snapshot["exeTarget"]!=os.readlink(f"/proc/{pid}/exe")):
            raise ProvenanceError("Process execution or cgroup changed during review")
        if audit.read_broker_start_ticks(pid)!=profile["startTicks"]:
            raise ProvenanceError("Process birth time changed after executable check")
        guard.assert_live()
        return report
    except (pidfd.PidfdError,audit.AuditError,UnicodeError,OSError,
            ValueError) as exc:
        if isinstance(exc,ProvenanceError):
            raise
        raise ProvenanceError("Trusted process provenance cannot be observed") from exc
    finally:
        if guard is not None:
            guard.close()


def inspect_pair(manifest):
    """Read-only both identities; successful observation never allows writing."""
    obj=validate_manifest(manifest)
    a=inspect_live_process(obj["profiles"]["broker"])
    b=inspect_live_process(obj["profiles"]["worker"])
    if (a.get("kernelLaunchFactsObserved") is not True or
            b.get("kernelLaunchFactsObserved") is not True):
        raise ProvenanceError("Both processes must be observed separately")
    return {**denial(),"kernelLaunchFactsObserved":True}
