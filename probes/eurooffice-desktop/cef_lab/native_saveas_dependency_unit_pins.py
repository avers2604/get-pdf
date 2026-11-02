#!/usr/bin/env python3
"""GD31J27aa: fixed root-owned offline dependencies and blocked unit inspection.

Necessary read-only manifest pins. NOT runtime import closure attestation,
ELF DT_NEEDED resolution, systemd launch proof, code signing, sandbox
certificate, native SaveAs, source-path discovery or authority to publish.

The manifest is never accepted via untrusted IPC. The only disk reader is
explicitly callable and constrained to fixed root-owned roots. No main().
"""
from __future__ import annotations

import ast
import hashlib
import json
import os
import re
import stat
import sys

MANIFEST_SCHEMA="getdoc-gd31j27aa-root-owned-dependency-unit-pins-lab/1"
RESULT_SCHEMA="getdoc-gd31j27aa-dependency-review-denial-lab/1"
FIXED_MANIFEST="/etc/getdoc-lab/gd31j27aa-dependencies.json"
MODULE_DIR="/opt/getdoc-lab/bin"
MODULE_NAMES=frozenset((
    "native_saveas_broker_listener",
    "native_saveas_worker_dialer",
    "native_saveas_fd_worker",
    "native_saveas_sandbox_observer",
))
UNIT_PATHS={
    "broker":"/etc/systemd/system/getdoc-gd31j27aa-broker.blocked.service",
    "worker":"/etc/systemd/system/getdoc-gd31j27aa-worker.blocked.service",
}
UNIT_USERS={"broker":"getdoc-lab-broker","worker":"getdoc-lab-worker"}
IPC_GROUP="getdoc-lab-ipc"
APPROVAL_PATH="/run/getdoc-gd31j27aa-disposable/APPROVAL_NEVER_AUTOCREATE"
LIB_DIRS=frozenset((
    "/usr/lib/x86_64-linux-gnu",
    "/usr/lib/aarch64-linux-gnu",
))
ALLOWED_STDLIB=frozenset((
    "__future__","os","sys","socket","stat","struct","json","hashlib",
    "re","array","fcntl",
))
MAX_MANIFEST_BYTES=8192
MAX_SINGLE_FILE=64*1024*1024
SHA=re.compile(r"[0-9a-f]{64}\Z",re.ASCII)
LIB_NAME=re.compile(r"[A-Za-z0-9_+.-]{1,90}\.so(?:\.[0-9]{1,4}){0,3}\Z",re.ASCII)


class DependencyError(ValueError):
    """Only generic diagnostic errors; never print file data or OS secrets."""


def _pairs_unique(items):
    data={}
    for k,v in items:
        if k in data:
            raise DependencyError("Duplicate pinned dependency manifest field")
        data[k]=v
    return data


def _hash(value):
    return type(value) is str and SHA.fullmatch(value) and value!="0"*64


def _library_name(path):
    if type(path) is not str or not path.startswith("/") or "\x00" in path:
        return False
    parent,slash,base=path.rpartition("/")
    return slash=="/" and parent in LIB_DIRS and bool(LIB_NAME.fullmatch(base))


def validate_manifest(data):
    if (type(data) is not dict or
            set(data)!={"schema","mode","laboratoryOnly",
                       "publishEnabled","modules","sharedObjects","units"} or
            data["schema"]!=MANIFEST_SCHEMA or
            data["mode"]!="inspect_only" or
            data["laboratoryOnly"] is not True or
            data["publishEnabled"] is not False or
            type(data["modules"]) is not dict or
            set(data["modules"])!=MODULE_NAMES or
            any(not _hash(v) for v in data["modules"].values()) or
            type(data["units"]) is not dict or
            set(data["units"])!=set(UNIT_PATHS) or
            any(not _hash(v) for v in data["units"].values()) or
            type(data["sharedObjects"]) is not dict or
            not 1<=len(data["sharedObjects"])<=16 or
            any(not _library_name(k) or not _hash(v)
                for k,v in data["sharedObjects"].items())):
        raise DependencyError("Unapproved root-owned dependency or unit pins")
    return data


def parse_manifest(raw):
    if (type(raw) is not bytes or not 4<=len(raw)<=MAX_MANIFEST_BYTES or
            raw.count(b"\n")!=1 or not raw.endswith(b"\n") or b"\r" in raw):
        raise DependencyError("Unbounded or noncanonical manifest packet")
    try:
        data=json.loads(raw[:-1].decode("ascii"),
                        object_pairs_hook=_pairs_unique,
                        parse_constant=lambda _: (_ for _ in ()).throw(
                            DependencyError("Nonfinite JSON constant")))
    except (ValueError,UnicodeError,RecursionError) as exc:
        if isinstance(exc,DependencyError):
            raise
        raise DependencyError("Invalid dependency manifest JSON") from exc
    return validate_manifest(data)


def denial():
    return {
        "schema":RESULT_SCHEMA,"state":"requires-independent-review",
        "pinnedFilesObserved":False,
        "dependencyClosureAttested":False,"runtimeLibrariesAttested":False,
        "unitLaunchAttested":False,"trustedRootManifestAttested":False,
        "nativePublicationAuthorized":False,"launchAuthorized":False,
        "writePermissionGranted":False,"releaseReady":False,
        "approvedForProduction":False,"laboratoryOnly":True,
    }


def check_static_imports(raw,approved_local_modules):
    """Reject unexpected top-level imported modules, eval/exec/importlib.

    Only source-code AST facts, NOT effective runtime dynamic imports.
    """
    if (type(raw) is not bytes or not raw or len(raw)>MAX_SINGLE_FILE or
            type(approved_local_modules) not in (set,frozenset) or
            approved_local_modules!=MODULE_NAMES):
        raise DependencyError("Unpinned or unbounded Python import source")
    try:
        tree=ast.parse(raw.decode("utf-8"),mode="exec")
    except (UnicodeError,SyntaxError,ValueError,MemoryError) as exc:
        raise DependencyError("Unparseable pinned Python source") from exc
    imports=set()
    for node in ast.walk(tree):
        if isinstance(node,ast.Import):
            imports.update(a.name.split(".",1)[0] for a in node.names)
        elif isinstance(node,ast.ImportFrom):
            if node.level!=0 or not node.module:
                raise DependencyError("Relative imports cannot be independently pinned")
            imports.add(node.module.split(".",1)[0])
        elif isinstance(node,ast.Call):
            if (isinstance(node.func,ast.Name) and
                    node.func.id in ("eval","exec","compile","__import__")):
                raise DependencyError("Dynamic interpreter loading in pinned source")
    if not imports.issubset(MODULE_NAMES|ALLOWED_STDLIB):
        raise DependencyError("Unexpected Python dependency outside fixed allowlist")
    return False


def verify_local_source(name,body,manifest):
    m=validate_manifest(manifest)
    if name not in MODULE_NAMES or type(body) is not bytes:
        raise DependencyError("Unknown fixed source role")
    if hashlib.sha256(body).hexdigest()!=m["modules"][name]:
        raise DependencyError("Source digest differs from root operator pin")
    return check_static_imports(body,MODULE_NAMES)


def _unit_sections(raw):
    if type(raw) is not bytes or not 16<=len(raw)<=16384:
        raise DependencyError("Unexpected or oversized unit policy")
    try:
        text=raw.decode("ascii")
    except UnicodeError as exc:
        raise DependencyError("Unit policy must be ASCII") from exc
    sections={}
    section=None
    for line in text.splitlines():
        line=line.strip()
        if not line or line.startswith("#"):
            continue
        if line in ("[Unit]","[Service]"):
            section=line[1:-1]
            if section in sections:
                raise DependencyError("Duplicate systemd unit section")
            sections[section]={}
        elif ("=" in line and section is not None and
              line.split("=",1)[0].strip().isidentifier()):
            key,value=line.split("=",1)
            key=key.strip()
            if key in sections[section]:
                raise DependencyError("Repeated systemd policy directive")
            sections[section][key]=value.strip()
        else:
            raise DependencyError("Unexpected systemd directive or section")
    if set(sections)!={"Unit","Service"}:
        raise DependencyError("Systemd unit sections must be exact")
    return sections


def verify_unit_bytes(role,body,manifest):
    m=validate_manifest(manifest)
    if role not in UNIT_PATHS or type(body) is not bytes or (
            hashlib.sha256(body).hexdigest()!=m["units"][role]):
        raise DependencyError("Blocked unit digest differs from pin")
    sections=_unit_sections(body)
    unit=sections["Unit"]
    service=sections["Service"]
    if (not unit.get("Description") or
            unit.get("RefuseManualStart")!="yes" or
            unit.get("ConditionPathExists")!=APPROVAL_PATH or
            set(unit)!={"Description","RefuseManualStart","ConditionPathExists"} or
            service!={
                "Type":"exec",
                "User":UNIT_USERS[role],
                "Group":IPC_GROUP,
                "ExecStart":"/usr/bin/false",
                "NoNewPrivileges":"yes",
                "PrivateNetwork":"yes",
                "PrivateMounts":"yes",
                "ProtectSystem":"strict",
                "ProtectHome":"yes",
                "RestrictAddressFamilies":"AF_UNIX",
                "RestrictSUIDSGID":"yes",
                "CapabilityBoundingSet":"",
                "AmbientCapabilities":"",
            }):
        raise DependencyError("Systemd policy is not blocked or exact")
    return False


def inspect_pinned_digest_snapshot(manifest,snapshot):
    """Compare independently supplied digest facts, NEVER certify file origin."""
    m=validate_manifest(manifest)
    expected={
        **{MODULE_DIR+"/"+name+".py":digest
           for name,digest in m["modules"].items()},
        **m["sharedObjects"],
        **{UNIT_PATHS[role]:digest for role,digest in m["units"].items()},
    }
    if type(snapshot) is not dict or snapshot!=expected:
        raise DependencyError("Pinned dependency snapshot incomplete or extended")
    return {**denial(),"pinnedFilesObserved":True}


def _fixed_components(path):
    """Only absolute known allowlisted files; no relative, magic or symlinks."""
    if type(path) is not str or (
            not path.startswith(MODULE_DIR+"/") and
            path not in UNIT_PATHS.values() and not _library_name(path)):
        raise DependencyError("Untrusted filesystem path outside pinned roots")
    items=path.split("/")
    if (len(items)<3 or items[0]!="" or
            any(not part or part in (".","..") for part in items[1:])):
        raise DependencyError("Untrusted fixed-path component")
    return items[1:]


def _read_fixed_file(path,*,required_mode=None):
    """Root-only fixed path traversal, O_NOFOLLOW on every component."""
    if not sys.platform.startswith("linux") or os.geteuid()!=0:
        raise DependencyError("Only separately provisioned root lab may read pins")
    parts=_fixed_components(path)
    flags=os.O_RDONLY|os.O_CLOEXEC|os.O_NOFOLLOW|os.O_NONBLOCK
    opened=[]
    try:
        d=os.open("/",flags|os.O_DIRECTORY)
        opened.append(d)
        for part in parts[:-1]:
            d=os.open(part,flags|os.O_DIRECTORY,dir_fd=d)
            opened.append(d)
            meta=os.fstat(d)
            if (not stat.S_ISDIR(meta.st_mode) or meta.st_uid!=0 or
                    meta.st_mode&0o022):
                raise DependencyError("Untrusted ancestor directory")
        f=os.open(parts[-1],flags,dir_fd=d)
        opened.append(f)
        before=os.fstat(f)
        if (not stat.S_ISREG(before.st_mode) or before.st_uid!=0 or
                before.st_nlink!=1 or before.st_mode&0o022 or
                (required_mode is not None and
                 before.st_mode&0o777!=required_mode) or
                not 1<=before.st_size<=MAX_SINGLE_FILE):
            raise DependencyError("Unsafe or oversized pinned inode")
        digest=hashlib.sha256()
        content=bytearray()
        while len(content)<before.st_size:
            part=os.read(f,min(65536,before.st_size-len(content)))
            if not part:
                raise DependencyError("Short read of pinned file")
            digest.update(part)
            content.extend(part)
        after=os.fstat(f)
        fields=("st_dev","st_ino","st_size","st_mtime_ns","st_ctime_ns")
        if any(getattr(before,x)!=getattr(after,x) for x in fields):
            raise DependencyError("Pinned file mutated during read")
        return bytes(content),digest.hexdigest()
    except OSError as exc:
        raise DependencyError("Root-owned dependency inaccessible") from exc
    finally:
        for fd in reversed(opened):
            try:
                os.close(fd)
            except OSError:
                pass


def load_root_manifest():
    body,_=_read_fixed_file(FIXED_MANIFEST,required_mode=0o600)
    if len(body)>MAX_MANIFEST_BYTES:
        raise DependencyError("Oversized pinned dependency JSON")
    return parse_manifest(body)


def inspect_root_owned_files():
    """Explicit root-only offline diagnostics, NOT imported by live code."""
    m=load_root_manifest()
    facts={}
    for name in sorted(MODULE_NAMES):
        path=MODULE_DIR+"/"+name+".py"
        content,digest=_read_fixed_file(path)
        verify_local_source(name,content,m)
        facts[path]=digest
    for path,expected in sorted(m["sharedObjects"].items()):
        _,digest=_read_fixed_file(path)
        if digest!=expected:
            raise DependencyError("Pinned system shared object changed")
        facts[path]=digest
    for role,path in UNIT_PATHS.items():
        content,digest=_read_fixed_file(path)
        verify_unit_bytes(role,content,m)
        facts[path]=digest
    return inspect_pinned_digest_snapshot(m,facts)
