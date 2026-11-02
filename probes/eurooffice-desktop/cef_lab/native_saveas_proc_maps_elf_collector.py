#!/usr/bin/env python3
"""GD31J27ad: bounded preopened /proc/maps + raw ELF64 DT_NEEDED lab.

No subprocess, ldd, readelf, systemd, /proc path open, file creation,
privilege change or network access. An INDEPENDENT disposable-VM supervisor
must supply already-open read-only, read-position-zero descriptors and
independently pinned expected process identities and ELF catalogue.

Important: receipt of arbitrary file descriptors does NOT attest provenance.
This module cannot prove these FDs are from the claimed process /proc/maps,
the completeness of mappings, their systemd unit, mount/MAC/BPF isolation,
root of trust or safe document publication. All claims stay deny-only.
"""
from __future__ import annotations

import os
import re
import struct

REPORT_SCHEMA="getdoc-gd31j27ad-preopened-kernel-maps-elf-review-denied/1"
MAX_MAPS_BYTES=256*1024
MAX_ELF_BYTES=64*1024*1024
MAX_MAPPINGS=2048
MAX_PHDRS=64
MAX_DYNAMIC=1024
MAP_HEADER=re.compile(
    r"([0-9a-f]+)-([0-9a-f]+) ([rwx-]{3}[ps]) "
    r"([0-9a-f]+) ([0-9a-f]{2}:[0-9a-f]{2}) ([0-9]+)",re.ASCII)
SONAME=re.compile(r"[A-Za-z0-9_+.-]{1,90}\.so(?:\.[0-9]{1,4}){0,3}\Z",re.ASCII)
ELF_DIRS=("/usr/lib/x86_64-linux-gnu/","/usr/lib/aarch64-linux-gnu/")
EXE=re.compile(r"/usr/bin/python3\.[0-9]{1,2}\Z",re.ASCII)
ANON=frozenset(("[heap]","[stack]","[vdso]","[vvar]","[vsyscall]"))


class CollectorError(ValueError):
    """Never echo process paths, private data or user-supplied file content."""


def denial():
    return {
        "schema":REPORT_SCHEMA,
        "state":"requires-independent-review",
        "kernelMapFactsObserved":False,
        "elfNeededFactsObserved":False,
        "mapsFdOriginAttested":False,
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


def _fixed_elf_path(path):
    return (type(path) is str and
            any(path.startswith(root) for root in ELF_DIRS) and
            type(path.rsplit("/",1)[-1]) is str and
            SONAME.fullmatch(path.rsplit("/",1)[-1]) is not None)


def parse_pinned_maps(data, *, executable, allowed_elf):
    """Parse bounded Linux maps and refuse ANY unlisted file mapping.

    This accepts only externally pinned executable and shared-object paths,
    plus basic known anonymous VMA names. It deliberately fails closed for
    novel kernel VMAs, JIT, anon labels, deleted maps and mmap of any file.
    It does not prove the buffer was read from a trusted kernel source.
    """
    if (type(data) is not bytes or not 1<=len(data)<=MAX_MAPS_BYTES or
            type(executable) is not str or EXE.fullmatch(executable) is None or
            type(allowed_elf) not in (set,frozenset) or not allowed_elf or
            any(not _fixed_elf_path(path) for path in allowed_elf)):
        raise CollectorError("Unpinned or oversized Linux memory map input")
    try:
        text=data.decode("ascii")
    except UnicodeError as exc:
        raise CollectorError("Invalid kernel maps encoding") from exc
    if not text.endswith("\n") or "\x00" in text:
        raise CollectorError("Incomplete kernel maps observation")
    lines=text.splitlines()
    if not 1<=len(lines)<=MAX_MAPPINGS:
        raise CollectorError("Unbounded Linux maps records")
    observed=set()
    executable_found=False
    previous_start=-1
    for line in lines:
        fields=line.split(None,5)
        if len(fields) not in (5,6):
            raise CollectorError("Malformed Linux memory mapping fields")
        header=" ".join(fields[:5])
        match=MAP_HEADER.fullmatch(header)
        if match is None:
            raise CollectorError("Malformed Linux memory map metadata")
        start,end=int(match.group(1),16),int(match.group(2),16)
        if not 0<=previous_start<start<end<2**64:
            raise CollectorError("Overlapping or unsorted memory map")
        previous_start=start
        inode=int(match.group(6))
        if len(fields)==5:
            if inode!=0:
                raise CollectorError("Unnamed file mapping is not permitted")
            continue
        path=fields[5]
        if not path or "\x00" in path or " (deleted)" in path:
            raise CollectorError("Removed or ambiguous mapped image")
        if path in ANON:
            if inode!=0:
                raise CollectorError("Anonymous mapping has file inode")
        elif path==executable:
            if inode<=0:
                raise CollectorError("Interpreter map has no source inode")
            executable_found=True
        elif path in allowed_elf:
            if inode<=0:
                raise CollectorError("ELF shared object has no source inode")
            observed.add(path)
        else:
            raise CollectorError("Unknown executable or mapped file outside allowlist")
    if not executable_found or observed!=set(allowed_elf):
        raise CollectorError("Pinned executable or library mapping missing")
    return tuple(sorted(observed))


def parse_elf64_needed(data, *, expected_machine):
    """Read ELF64 LE PT_DYNAMIC/DT_NEEDED from bounded raw bytes only.

    Supports ET_EXEC/ET_DYN for x86_64 or aarch64. Rejects all incomplete,
    ambiguous or unsupported images rather than attempting unsafe parsing
    or invoking external ELF utilities. This does NOT resolve dlopen.
    """
    if (type(data) is not bytes or not 64<=len(data)<=MAX_ELF_BYTES or
            type(expected_machine) is not int or
            expected_machine not in (62,183) or
            data[:6]!=b"\x7fELF\x02\x01" or
            data[6]!=1 or data[7]!=0):
        raise CollectorError("Unsupported ELF64 little-endian image")
    try:
        fields=struct.unpack_from("<HHIQQQIHHHHHH",data,16)
    except struct.error as exc:
        raise CollectorError("Incomplete ELF header") from exc
    e_type,e_machine,e_version,_,phoff,_,_,ehsize,phentsize,phnum,_,_,_=fields
    if (e_type not in (2,3) or e_machine!=expected_machine or e_version!=1 or
            ehsize!=64 or phentsize!=56 or not 1<=phnum<=MAX_PHDRS or
            phoff<64 or phoff+phnum*56>len(data)):
        raise CollectorError("Untrusted ELF program header table")
    loads=[]
    dynamic=None
    try:
        for i in range(phnum):
            kind,flags,offset,vaddr,paddr,filesz,memsz,align=struct.unpack_from(
                "<IIQQQQQQ",data,phoff+i*56)
            if offset>len(data) or filesz>len(data)-offset:
                raise CollectorError("ELF segment beyond file boundaries")
            if kind==1:
                if filesz>memsz or filesz==0:
                    raise CollectorError("Unsupported ELF LOAD segment")
                loads.append((offset,vaddr,filesz))
            elif kind==2:
                if dynamic is not None or not 16<=filesz<=MAX_DYNAMIC*16 or
                        filesz%16!=0:
                    raise CollectorError("Ambiguous ELF dynamic segment")
                dynamic=(offset,filesz)
    except struct.error as exc:
        raise CollectorError("Malformed ELF64 program headers") from exc
    if dynamic is None or not loads:
        raise CollectorError("ELF dynamic table or LOAD mapping missing")
    offset,filesz=dynamic
    needed=[]
    string_address=None
    string_size=None
    terminated=False
    try:
        for position in range(offset,offset+filesz,16):
            tag,val=struct.unpack_from("<QQ",data,position)
            if tag==0:
                terminated=True
                break
            if tag==1:
                needed.append(val)
            elif tag==5:
                if string_address is not None:
                    raise CollectorError("Duplicate ELF string table address")
                string_address=val
            elif tag==10:
                if string_size is not None:
                    raise CollectorError("Duplicate ELF string table size")
                string_size=val
    except struct.error as exc:
        raise CollectorError("Truncated ELF dynamic entries") from exc
    if (not terminated or string_address is None or
            type(string_size) is not int or
            not 1<=string_size<=65536 or len(needed)>128):
        raise CollectorError("No bounded terminated ELF dynamic string table")
    regions=[]
    for load_offset,load_vaddr,load_size in loads:
        if (load_vaddr<=string_address and
                string_address-load_vaddr<=load_size and
                string_size<=load_size-(string_address-load_vaddr)):
            regions.append(load_offset+string_address-load_vaddr)
    if len(regions)!=1:
        raise CollectorError("ELF dynamic string table virtual address ambiguous")
    string_start=regions[0]
    table=data[string_start:string_start+string_size]
    names=[]
    for item in needed:
        if item>=len(table):
            raise CollectorError("ELF DT_NEEDED string outside table")
        stop=table.find(b"\x00",item)
        if stop<item:
            raise CollectorError("ELF DT_NEEDED string unterminated")
        try:
            name=table[item:stop].decode("ascii")
        except UnicodeError as exc:
            raise CollectorError("Non-ASCII DT_NEEDED library name") from exc
        if SONAME.fullmatch(name) is None:
            raise CollectorError("Unsafe dynamic library SONAME")
        names.append(name)
    if len(names)!=len(set(names)):
        raise CollectorError("Duplicate ELF DT_NEEDED dependency")
    return tuple(sorted(names))


def compare_elf_needed(expected,observed):
    if (type(expected) is not dict or type(observed) is not dict or
            set(expected)!=set(observed) or not expected):
        raise CollectorError("Missing or unexpected ELF binary")
    for path,children in expected.items():
        if (not _fixed_elf_path(path) or type(children) not in (tuple,list) or
                type(observed[path]) not in (tuple,list) or
                any(type(x) is not str or SONAME.fullmatch(x) is None
                    for x in children) or
                tuple(sorted(set(children)))!=tuple(children) or
                tuple(children)!=tuple(observed[path])):
            raise CollectorError("ELF DT_NEEDED differs from independent pin")
    return False


def _read_bounded_preopened_fd(fd,limit):
    """Read from one caller-owned FD; never open/seek/close it."""
    if (type(fd) is not int or fd<3 or
            type(limit) is not int or not 1<=limit<=MAX_ELF_BYTES):
        raise CollectorError("Unpinned externally opened OS descriptor")
    body=bytearray()
    try:
        while len(body)<=limit:
            chunk=os.read(fd,min(65536,limit+1-len(body)))
            if not chunk:
                break
            body.extend(chunk)
    except (OSError,MemoryError) as exc:
        raise CollectorError("Preopened read-only OS evidence unavailable") from exc
    if not body or len(body)>limit:
        raise CollectorError("Missing or oversized preopened OS evidence")
    return bytes(body)


def capture_preopened_once(*,proc_map_fds,elf_fds,expected_pids,
                           executable_by_role,expected_elf_paths,
                           expected_needed,approved_disposable_vm,
                           expected_machine):
    """Consume bounded already-open FD bytes; no OS provenance certificate.

    The source of passed FDs is UNTRUSTED unless a separate independent
    OS supervisor authenticates pidfd, procfs FD, ELF inode and source
    identities. This function provides only parser/comparison observations.
    FDs remain owned by the caller and are NOT closed here.
    """
    roles={"broker","worker"}
    if (approved_disposable_vm is not True or
            type(proc_map_fds) is not dict or set(proc_map_fds)!=roles or
            type(expected_pids) is not dict or set(expected_pids)!=roles or
            any(type(v) is not int or not 1<=v<2**31
                for v in expected_pids.values()) or
            expected_pids["broker"]==expected_pids["worker"] or
            type(executable_by_role) is not dict or
            set(executable_by_role)!=roles or
            any(type(x) is not str or EXE.fullmatch(x) is None
                for x in executable_by_role.values()) or
            type(expected_elf_paths) not in (set,frozenset) or
            not expected_elf_paths or
            any(not _fixed_elf_path(x) for x in expected_elf_paths) or
            type(expected_needed) is not dict or
            set(expected_needed)!=expected_elf_paths or
            type(elf_fds) is not dict or
            set(elf_fds)!=expected_elf_paths or
            type(expected_machine) is not int or
            expected_machine not in (62,183)):
        raise CollectorError("Unpinned or unapproved disposable VM evidence")
    handles=[*proc_map_fds.values(),*elf_fds.values()]
    if (any(type(fd) is not int or fd<3 for fd in handles) or
            len(handles)!=len(set(handles))):
        raise CollectorError("Missing or aliased OS evidence descriptor")
    observed=set()
    for role in ("broker","worker"):
        content=_read_bounded_preopened_fd(proc_map_fds[role],MAX_MAPS_BYTES)
        observed.update(parse_pinned_maps(
            content,executable=executable_by_role[role],
            allowed_elf=expected_elf_paths))
    if observed!=expected_elf_paths:
        raise CollectorError("Mapped ELF inventory differs from root pin")
    needed={}
    for path in sorted(expected_elf_paths):
        image=_read_bounded_preopened_fd(elf_fds[path],MAX_ELF_BYTES)
        needed[path]=parse_elf64_needed(image,expected_machine=expected_machine)
    compare_elf_needed(expected_needed,needed)
    return {**denial(),"kernelMapFactsObserved":True,
            "elfNeededFactsObserved":True}
