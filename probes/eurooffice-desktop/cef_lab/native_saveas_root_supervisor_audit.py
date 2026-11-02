#!/usr/bin/env python3
"""GD31J27u: conservative read-only root OS prerequisites, NEVER attestation.

This checks actual /proc and broker process birth time before any FD
preparation, but cannot prove mount contents, BPF allowlist, Astra MAC,
independent executable identity or permission to publish files.
Requires independent disposable Astra VM audit even on positive observations.
"""
from __future__ import annotations

import os
import re
import sys

import native_saveas_sandbox_observer as observer
import native_saveas_supervisor_handoff as proc_observer

SCHEMA="getdoc-gd31j27u-root-dac-namespace-observation-lab/1"
DAC_READ_SEARCH_BIT=1<<2   # Linux CAP_DAC_READ_SEARCH; never CAP_DAC_OVERRIDE
CAP_KEYS=("CapInh","CapPrm","CapEff","CapBnd","CapAmb")
HEX_CAP=re.compile(r"[0-9a-fA-F]{1,16}\Z")

class AuditError(ValueError):
    """No sensitive mounts, paths, contents or process data in errors."""


def _single_root_identity(value):
    items=value.split()
    return (len(items)==4 and all(part=="0" for part in items))


def inspect_root_observation(*,root_status_text,mountinfo_text,
                             root_mount_ns_inode,host_mount_ns_inode,
                             root_net_ns_inode,host_net_ns_inode):
    """Evaluate necessary facts, not trust or clearance."""
    try:
        status=observer.parse_status(root_status_text)
        mounts=observer.parse_mountinfo(mountinfo_text)
    except observer.ObservationError as exc:
        raise AuditError("Missing or malformed root kernel observations") from exc
    if (not _single_root_identity(status["Uid"]) or
            not _single_root_identity(status["Gid"]) or
            status["NoNewPrivs"]!="1" or status["Seccomp"]!="2" or
            not status["Seccomp_filters"].isdecimal() or
            not 1<=int(status["Seccomp_filters"])<=32 or
            status["TracerPid"]!="0" or
            any(type(n) is not int or n<=0 for n in (
                root_mount_ns_inode,host_mount_ns_inode,
                root_net_ns_inode,host_net_ns_inode)) or
            root_mount_ns_inode==host_mount_ns_inode or
            root_net_ns_inode==host_net_ns_inode):
        raise AuditError("Root service kernel prerequisites refused")
    capabilities={}
    for key in CAP_KEYS:
        value=status[key]
        if not HEX_CAP.fullmatch(value):
            raise AuditError("Unparseable root capability set")
        bitset=int(value,16)
        if bitset & ~DAC_READ_SEARCH_BIT:
            raise AuditError("Root service has unexpected privilege")
        capabilities[key]=bitset
    if (capabilities["CapInh"]!=0 or capabilities["CapAmb"]!=0 or
            capabilities["CapEff"] & ~capabilities["CapPrm"] or
            capabilities["CapPrm"] & ~capabilities["CapBnd"]):
        raise AuditError("Root capability subsets inconsistent")
    # CAP_DAC_READ_SEARCH is broad: observation does NOT make it safe.
    return {
        "schema":SCHEMA,"state":"requires-independent-review",
        "kernelFactsObserved":True,
        "mountEntriesObserved":mounts,
        "dacReadSearchObserved":bool(capabilities["CapEff"] & DAC_READ_SEARCH_BIT),
        "rootSandboxAttested":False,"manifestTrustedByOs":False,
        "corporateFilesAbsentAttested":False,
        "seccompAllowlistAttested":False,
        "mountRestrictionAttested":False,
        "brokerProcessPinnedAttested":False,
        "nativePublicationAuthorized":False,"launchAuthorized":False,
        "writePermissionGranted":False,"releaseReady":False,
        "approvedForProduction":False,"laboratoryOnly":True,
    }


def read_broker_start_ticks(pid:int)->int:
    if (not sys.platform.startswith("linux") or
            type(pid) is not int or not 1<=pid<2**31):
        raise AuditError("Broker PID unavailable")
    try:
        return proc_observer._proc_start_ticks(pid)
    except (proc_observer.HandoffError,OSError,ValueError) as exc:
        raise AuditError("Broker lifetime kernel evidence inaccessible") from exc


def require_broker_lifetime(*,broker_pid,expected_start_ticks):
    """Necessary PID reuse guard; FALSE is never attestation or approval."""
    if (type(broker_pid) is not int or not 1<=broker_pid<2**31 or
            type(expected_start_ticks) is not int or expected_start_ticks<=0):
        raise AuditError("Unpinned broker process lifetime")
    if read_broker_start_ticks(broker_pid)!=expected_start_ticks:
        raise AuditError("Connected broker process lifetime mismatch")
    return False


def inspect_current_root():
    """Read actual kernel facts without creating anything or changing UID."""
    if not sys.platform.startswith("linux") or os.getuid()!=0 or os.geteuid()!=0:
        raise AuditError("Not an existing root-only Linux process")
    try:
        mounts={k:os.stat(f"/proc/self/ns/{k}").st_ino for k in ("mnt","net")}
        host={k:os.stat(f"/proc/1/ns/{k}").st_ino for k in ("mnt","net")}
        root_status=observer._limited_read("/proc/self/status",observer.MAX_STATUS)
        mount_info=observer._limited_read("/proc/self/mountinfo",
                                          observer.MAX_MOUNTINFO)
    except (OSError,observer.ObservationError) as exc:
        raise AuditError("Root namespace/kernel facts inaccessible") from exc
    return inspect_root_observation(
        root_status_text=root_status,mountinfo_text=mount_info,
        root_mount_ns_inode=mounts["mnt"],host_mount_ns_inode=host["mnt"],
        root_net_ns_inode=mounts["net"],host_net_ns_inode=host["net"],
    )
