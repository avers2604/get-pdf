#!/usr/bin/env python3
"""GD31J27ae: root-only read-only process/FD-origin necessary observations.

UNWIRED LAB MODULE. No main, subprocess, setuid, service installation,
socket transfer, open, unlink, rename or document SaveAs. An externally
provisioned disposable VM root supervisor must supply all preopened FDs and
independently anchored PIDs/startTicks/namespaces/ELF SHA pins.

Checks observed /proc/self/fd/<FD> targets before AND after one read,
pins each process with a Linux pidfd, verifies namespace inode snapshots
before/after the parser and hashes exact root-owned ELF inode via pread.
A root PID, path string, namespace inode and SHA cannot by themselves
attest full supervisor trust, Astra MAC, mounts, systemd unit, or security.
Every output stays review-only; no release permission can ever be granted.
"""
from __future__ import annotations

import hashlib
import hmac
import os
import re
import stat
import sys

if sys.platform.startswith("linux"):
    import fcntl
else:
    fcntl=None

import native_saveas_pidfd_provenance as pidfd
import native_saveas_proc_maps_elf_collector as maps

REPORT_SCHEMA="getdoc-gd31j27ae-fd-path-pidfd-namespace-observation-deny-only/1"
NAMESPACES=("mnt","net","pid")
ROLES=("broker","worker")
SHA64=re.compile(r"[0-9a-f]{64}\Z",re.ASCII)


class OriginError(ValueError):
    """No identifying paths, process details, mapped memory or data."""


def denial():
    return {
        "schema":REPORT_SCHEMA,
        "state":"requires-independent-review",
        "kernelFdPathObserved":False,
        "kernelNamespaceFactsObserved":False,
        "elfRootOwnedShaObserved":False,
        "fdOriginAttested":False,
        "processIdentityAttested":False,
        "systemdUnitAttested":False,
        "osEvidenceAttested":False,
        "runtimeLibrariesAttested":False,
        "dependencyClosureAttested":False,
        "launchAuthorized":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "releaseReady":False,
        "approvedForProduction":False,
        "laboratoryOnly":True,
    }


def _valid_inputs(*,proc_map_fds,elf_fds,expected_pids,
                  expected_start_ticks,expected_namespaces,
                  expected_elf_sha256,executable_by_role,
                  expected_elf_paths,expected_needed,expected_machine,
                  approved_disposable_vm):
    roles=set(ROLES)
    if (approved_disposable_vm is not True or
            not sys.platform.startswith("linux") or fcntl is None or
            type(proc_map_fds) is not dict or set(proc_map_fds)!=roles or
            type(expected_pids) is not dict or set(expected_pids)!=roles or
            type(expected_start_ticks) is not dict or
            set(expected_start_ticks)!=roles or
            type(expected_namespaces) is not dict or
            set(expected_namespaces)!=roles or
            type(executable_by_role) is not dict or
            set(executable_by_role)!=roles or
            any(type(v) is not str or maps.EXE.fullmatch(v) is None
                for v in executable_by_role.values()) or
            type(expected_elf_paths) not in (set,frozenset) or
            not expected_elf_paths or
            any(not maps._fixed_elf_path(x) for x in expected_elf_paths) or
            type(elf_fds) is not dict or
            set(elf_fds)!=expected_elf_paths or
            type(expected_needed) is not dict or
            set(expected_needed)!=expected_elf_paths or
            type(expected_elf_sha256) is not dict or
            set(expected_elf_sha256)!=expected_elf_paths or
            any(type(v) is not str or SHA64.fullmatch(v) is None or
                v=="0"*64 for v in expected_elf_sha256.values()) or
            type(expected_machine) is not int or
            expected_machine not in (62,183)):
        raise OriginError("Untrusted OS evidence inputs or declared lab scope")
    handles=[*proc_map_fds.values(),*elf_fds.values()]
    if (any(type(fd) is not int or fd<3 for fd in handles) or
            len(handles)!=len(set(handles)) or
            any(type(expected_pids[role]) is not int or
                not 1<=expected_pids[role]<2**31 or
                type(expected_start_ticks[role]) is not int or
                expected_start_ticks[role]<=0 for role in ROLES) or
            expected_pids["broker"]==expected_pids["worker"]):
        raise OriginError("Pinned PID or owned file descriptor missing")
    for role in ROLES:
        values=expected_namespaces[role]
        if (type(values) is not dict or
                set(values)!=set(NAMESPACES) or
                any(type(inode) is not int or inode<=0
                    for inode in values.values())):
            raise OriginError("Kernel namespace pins incomplete")
    for parent,needed in expected_needed.items():
        if (type(needed) not in (list,tuple) or
                needed!=sorted(set(needed)) and
                tuple(needed)!=tuple(sorted(set(needed))) or
                any(type(n) is not str or maps.SONAME.fullmatch(n) is None
                    for n in needed)):
            raise OriginError("Unexpected ELF dependency pin")


def _kernel_fd_targets(proc_map_fds,elf_fds,expected_pids):
    """Match Linux proc/self/fd readlinks to ONLY pinned target paths."""
    expected={
        proc_map_fds[role]:f"/proc/{expected_pids[role]}/maps"
        for role in ROLES
    }
    expected.update({fd:path for path,fd in elf_fds.items()})
    for fd,target in expected.items():
        observed=os.readlink(f"/proc/self/fd/{fd}")
        if observed!=target:
            raise OriginError("Kernel FD link is not the pinned proc/ELF target")
    return False


def _namespace_snapshot(expected_pids,expected_namespaces):
    """Stat fixed /proc/PID/ns links, not file contents or arbitrary names."""
    for role in ROLES:
        pid=expected_pids[role]
        for ns in NAMESPACES:
            found=os.stat(f"/proc/{pid}/ns/{ns}").st_ino
            if type(found) is not int or found!=expected_namespaces[role][ns]:
                raise OriginError("Process namespace changed from operator pin")
    return False


def _hash_root_elf_fd(fd):
    """Hash exact regular root-owned read-only inode via pread, no offset swap."""
    if (not sys.platform.startswith("linux") or fcntl is None or
            type(fd) is not int or fd<3):
        raise OriginError("Unpinned ELF descriptor")
    before=os.fstat(fd)
    mode=fcntl.fcntl(fd,fcntl.F_GETFL) & os.O_ACCMODE
    if (not stat.S_ISREG(before.st_mode) or mode!=os.O_RDONLY or
            before.st_uid!=0 or before.st_nlink!=1 or
            before.st_mode&0o022 or
            not 1<=before.st_size<=maps.MAX_ELF_BYTES):
        raise OriginError("ELF descriptor is not immutable root-owned input")
    digest=hashlib.sha256()
    offset=0
    while offset<before.st_size:
        part=os.pread(fd,min(65536,before.st_size-offset),offset)
        if not part:
            raise OriginError("Unexpected end of pinned ELF data")
        digest.update(part)
        offset+=len(part)
    after=os.fstat(fd)
    for field in ("st_dev","st_ino","st_size","st_mtime_ns","st_ctime_ns"):
        if getattr(before,field)!=getattr(after,field):
            raise OriginError("Pinned ELF inode mutated during hash")
    return digest.hexdigest()


def review_fd_origins_once(*,proc_map_fds,elf_fds,expected_pids,
                           expected_start_ticks,expected_namespaces,
                           expected_elf_sha256,executable_by_role,
                           expected_elf_paths,expected_needed,
                           expected_machine,approved_disposable_vm):
    """Single bounded observation; consumes no authority and closes pidfds.

    This root-only read-only function does not acquire root. It depends on a
    trusted external supervisor to prove how the preopened FDs and pins were
    acquired. os.readlink and os.stat are observations, not provenance proof.
    No retry if any identity changes before or after capture.
    """
    _valid_inputs(
        proc_map_fds=proc_map_fds,elf_fds=elf_fds,
        expected_pids=expected_pids,expected_start_ticks=expected_start_ticks,
        expected_namespaces=expected_namespaces,
        expected_elf_sha256=expected_elf_sha256,
        executable_by_role=executable_by_role,
        expected_elf_paths=expected_elf_paths,
        expected_needed=expected_needed,expected_machine=expected_machine,
        approved_disposable_vm=approved_disposable_vm)
    if os.geteuid()!=0:
        raise OriginError("Independent root-only disposable VM observer required")
    guards={}
    close_failed=False
    try:
        guards["broker"]=pidfd.pin_broker_process(
            expected_pids["broker"],expected_start_ticks["broker"])
        guards["worker"]=pidfd.pin_worker_process(
            expected_pids["worker"],expected_start_ticks["worker"])
        for role in ROLES:
            guards[role].assert_live()
        _kernel_fd_targets(proc_map_fds,elf_fds,expected_pids)
        _namespace_snapshot(expected_pids,expected_namespaces)
        for path in sorted(expected_elf_paths):
            actual=_hash_root_elf_fd(elf_fds[path])
            if not hmac.compare_digest(actual,expected_elf_sha256[path]):
                raise OriginError("Root-owned ELF digest mismatched trusted pin")
        report=maps.capture_preopened_once(
            proc_map_fds=proc_map_fds,elf_fds=elf_fds,
            expected_pids=expected_pids,
            executable_by_role=executable_by_role,
            expected_elf_paths=expected_elf_paths,
            expected_needed=expected_needed,
            expected_machine=expected_machine,
            approved_disposable_vm=approved_disposable_vm)
        if (type(report) is not dict or
                report.get("kernelMapFactsObserved") is not True or
                report.get("elfNeededFactsObserved") is not True or
                any(report.get(k) is not False for k in (
                    "mapsFdOriginAttested","osEvidenceAttested",
                    "nativePublicationAuthorized","releaseReady"))):
            raise OriginError("Underlying collector returned unsafe authority")
        _kernel_fd_targets(proc_map_fds,elf_fds,expected_pids)
        _namespace_snapshot(expected_pids,expected_namespaces)
        for path in sorted(expected_elf_paths):
            actual=_hash_root_elf_fd(elf_fds[path])
            if not hmac.compare_digest(actual,expected_elf_sha256[path]):
                raise OriginError("Root ELF changed during kernel evidence capture")
        for role in ROLES:
            guards[role].assert_live()
        return {**denial(),"kernelFdPathObserved":True,
                "kernelNamespaceFactsObserved":True,
                "elfRootOwnedShaObserved":True}
    except (OSError,ValueError,AttributeError) as exc:
        raise OriginError("Pinned kernel FD origin review refused") from exc
    finally:
        for guard in reversed(list(guards.values())):
            try:
                guard.close()
            except (OSError,pidfd.PidfdError):
                close_failed=True
        if close_failed:
            raise OriginError("Cannot close process lifetime references")
