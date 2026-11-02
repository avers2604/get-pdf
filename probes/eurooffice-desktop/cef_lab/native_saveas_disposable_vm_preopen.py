#!/usr/bin/env python3
"""GD31J27af — explicit, read-only disposable-VM FD preopen pilot.

NO MAIN / NO SERVICE / NO FORK / NO UID CHANGES / NO SAVEAS.
Only call manually from an independently approved OFFLINE, disposable
Astra Linux VM. Already-running UID0 is REQUIRED; this cannot acquire it.
The caller supplies independent pinned PIDs, startTicks, namespace inodes,
ELF hashes and DT_NEEDED. All are untrusted unless vetted out of band.

Opens exactly two procfs /proc/PID/maps and allowlisted root-owned ELF
through anchored O_NOFOLLOW dirfd walks, holds outer process pidfds while
opening, then delegates to the existing read-only denial-only verifier.
No process or service is launched; no systemd, network, user file or
IPC access. Even positive observations are not an OS trust certificate.
"""
from __future__ import annotations

import os
import stat
import sys

import native_saveas_fd_origin_namespace as origin
import native_saveas_pidfd_provenance as pidfd
import native_saveas_proc_maps_elf_collector as maps

EXPLICIT_ACK="OFFLINE_DISPOSABLE_VM_READONLY_DIAGNOSTIC_ONLY"
REPORT_SCHEMA="getdoc-gd31j27af-manual-fixed-fd-open-deny-only/1"
ROLES=("broker","worker")


class PreopenError(ValueError):
    """Sanitized; no paths, usernames, file contents or process IDs."""


def denial():
    return {
        **origin.denial(),
        "schema":REPORT_SCHEMA,
        "fixedReadOnlyDescriptorsOpened":False,
        "externalOperatorApprovalAttested":False,
        "trustedVmIsolationAttested":False,
        "effectiveSystemdAttested":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "releaseReady":False,
        "approvedForProduction":False,
    }


def _validate_inputs(*,expected_pids,expected_start_ticks,
                     expected_namespaces,expected_elf_sha256,
                     executable_by_role,expected_needed,expected_machine,
                     approved_disposable_vm,operator_ack):
    if (approved_disposable_vm is not True or
            type(operator_ack) is not str or operator_ack!=EXPLICIT_ACK or
            not sys.platform.startswith("linux") or
            type(expected_elf_sha256) is not dict or
            not 1<=len(expected_elf_sha256)<=16):
        raise PreopenError("Independent offline VM manual gate not satisfied")
    names=set(expected_elf_sha256)
    synthetic_fds={path:30+i for i,path in enumerate(sorted(names))}
    try:
        origin._valid_inputs(
            proc_map_fds={"broker":20,"worker":21},
            elf_fds=synthetic_fds,
            expected_pids=expected_pids,
            expected_start_ticks=expected_start_ticks,
            expected_namespaces=expected_namespaces,
            expected_elf_sha256=expected_elf_sha256,
            executable_by_role=executable_by_role,
            expected_elf_paths=names,
            expected_needed=expected_needed,
            expected_machine=expected_machine,
            approved_disposable_vm=approved_disposable_vm,
        )
    except (origin.OriginError,TypeError,ValueError) as exc:
        raise PreopenError("Invalid pinned process or ELF input policy") from exc


def _open_fixed_components(components,*,is_proc=False):
    """Return one read-only leaf FD, close every intermediate directory.

    All names are computed by a hardcoded pattern + previously validated
    manifest IDs. There is no arbitrary path parameter, and no symlinks or
    file creation. Paths beneath /usr/lib require root-owned ancestors
    and root-owned regular ELF leaf. A proc PID directory is owned by its
    own nonroot user and therefore has a separate read-only policy.
    """
    if (type(components) not in (tuple,list) or not components or
            any(type(x) is not str or not x or x in (".","..") or
                "/" in x or "\x00" in x for x in components)):
        raise PreopenError("Unsafe fixed OS evidence path components")
    flags=os.O_RDONLY|os.O_CLOEXEC|os.O_NOFOLLOW|os.O_NONBLOCK
    folders=[]
    leaf=-1
    failed_close=False
    try:
        current=os.open("/",flags|os.O_DIRECTORY)
        folders.append(current)
        for i,part in enumerate(components[:-1]):
            current=os.open(part,flags|os.O_DIRECTORY,dir_fd=current)
            folders.append(current)
            meta=os.fstat(current)
            if (not stat.S_ISDIR(meta.st_mode) or meta.st_mode&0o022 or
                    (not is_proc or i!=1) and meta.st_uid!=0):
                raise PreopenError("Unsafe pinned OS directory ownership")
        leaf=os.open(components[-1],flags,dir_fd=current)
        meta=os.fstat(leaf)
        if (leaf<3 or not stat.S_ISREG(meta.st_mode) or
                (not is_proc and (meta.st_uid!=0 or meta.st_nlink!=1 or
                                  meta.st_mode&0o022))):
            raise PreopenError("Pinned read-only OS evidence inode rejected")
    except (OSError,ValueError,AttributeError) as exc:
        if leaf>=0:
            try:
                os.close(leaf)
            except OSError:
                pass
        if isinstance(exc,PreopenError):
            raise
        raise PreopenError("Pinned OS evidence path cannot be opened") from exc
    finally:
        for fd in reversed(folders):
            try:
                os.close(fd)
            except OSError:
                failed_close=True
        if failed_close:
            if leaf>=0:
                try:
                    os.close(leaf)
                except OSError:
                    pass
            raise PreopenError("Cannot close temporary directory handles")
    return leaf


def _open_fixed_proc_map(pid):
    if type(pid) is not int or not 1<=pid<2**31:
        raise PreopenError("Untrusted process for procfs evidence")
    return _open_fixed_components(("proc",str(pid),"maps"),is_proc=True)


def _open_fixed_elf(path):
    if not maps._fixed_elf_path(path):
        raise PreopenError("ELF file is outside the fixed allowlist")
    return _open_fixed_components(tuple(path[1:].split("/")))


def preopen_disposable_once(*,expected_pids,expected_start_ticks,
                            expected_namespaces,expected_elf_sha256,
                            executable_by_role,expected_needed,
                            expected_machine,approved_disposable_vm,
                            operator_ack):
    """Explicit-only one-shot read-only FD provisioning. NEVER production.

    Observe process lifetime before opening its /proc maps. The preexisting
    origin.review_fd_origins_once pins again and validates namespace, SHA,
    FD targets, memory maps and ELF. We close all our FDs and guards even
    on exceptions; no retry and no authority is ever returned.
    """
    _validate_inputs(
        expected_pids=expected_pids,
        expected_start_ticks=expected_start_ticks,
        expected_namespaces=expected_namespaces,
        expected_elf_sha256=expected_elf_sha256,
        executable_by_role=executable_by_role,
        expected_needed=expected_needed,
        expected_machine=expected_machine,
        approved_disposable_vm=approved_disposable_vm,
        operator_ack=operator_ack,
    )
    if os.geteuid()!=0:
        raise PreopenError("Only the separately provisioned VM root observer may collect")
    handles=[]
    guards={}
    failed_close=False
    try:
        guards["broker"]=pidfd.pin_broker_process(
            expected_pids["broker"],expected_start_ticks["broker"])
        guards["worker"]=pidfd.pin_worker_process(
            expected_pids["worker"],expected_start_ticks["worker"])
        for role in ROLES:
            guards[role].assert_live()
        proc_fds={}
        for role in ROLES:
            fd=_open_fixed_proc_map(expected_pids[role])
            if type(fd) is not int or fd<3 or fd in handles:
                raise PreopenError("Unexpected or repeated kernel procfs descriptor")
            handles.append(fd)
            proc_fds[role]=fd
        elf_fds={}
        for path in sorted(expected_elf_sha256):
            fd=_open_fixed_elf(path)
            if type(fd) is not int or fd<3 or fd in handles:
                raise PreopenError("Unexpected or repeated kernel ELF descriptor")
            handles.append(fd)
            elf_fds[path]=fd
        result=origin.review_fd_origins_once(
            proc_map_fds=proc_fds,
            elf_fds=elf_fds,
            expected_pids=expected_pids,
            expected_start_ticks=expected_start_ticks,
            expected_namespaces=expected_namespaces,
            expected_elf_sha256=expected_elf_sha256,
            executable_by_role=executable_by_role,
            expected_elf_paths=set(expected_elf_sha256),
            expected_needed=expected_needed,
            expected_machine=expected_machine,
            approved_disposable_vm=True,
        )
        if (type(result) is not dict or
                any(result.get(field) is not True for field in (
                    "kernelFdPathObserved","kernelNamespaceFactsObserved",
                    "elfRootOwnedShaObserved")) or
                any(result.get(field) is not False for field in (
                    "fdOriginAttested","osEvidenceAttested",
                    "nativePublicationAuthorized","writePermissionGranted",
                    "releaseReady","approvedForProduction"))):
            raise PreopenError("OS source review returned unapproved authority")
        for role in ROLES:
            guards[role].assert_live()
        return {**denial(),"fixedReadOnlyDescriptorsOpened":True}
    except (OSError,ValueError,AttributeError) as exc:
        raise PreopenError("One-shot read-only VM FD preflight refused") from exc
    finally:
        for fd in reversed(handles):
            try:
                os.close(fd)
            except OSError:
                failed_close=True
        for guard in reversed(list(guards.values())):
            try:
                guard.close()
            except (OSError,pidfd.PidfdError):
                failed_close=True
        if failed_close:
            raise PreopenError("Cannot close one-shot VM evidence handles")
