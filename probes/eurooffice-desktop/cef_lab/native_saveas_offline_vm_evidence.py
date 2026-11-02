#!/usr/bin/env python3
"""GD31J27ac: verify in-memory wheel RECORD and compare external OS evidence.

Unwired and offline. The caller must provide already captured external
systemd/ELF/process facts; this module NEVER launches OS commands, opens
device/procfs paths, installs a wheel, imports wheel contents or writes files.
The independent provenance and completeness of snapshots are UNPROVEN.

A signed catalog constrains declared inventory, not full ELF/Python runtime
closure. All outputs remain review-only, never authorize SaveAs.
"""
from __future__ import annotations

import base64
import csv
import hashlib
import hmac
import io
import json
from pathlib import PurePosixPath
import re
import stat
import zipfile

import native_saveas_offline_sbom_evidence as sbom

SNAPSHOT_SCHEMA="getdoc-gd31j27ac-offline-external-os-snapshot-untrusted/1"
REPORT_SCHEMA="getdoc-gd31j27ac-offline-evidence-review-denial/1"
MAX_WHEEL_BYTES=16*1024*1024
MAX_UNCOMPRESSED_BYTES=8*1024*1024
MAX_ZIP_ENTRIES=256
HEX_SHA=re.compile(r"[0-9a-f]{64}\Z",re.ASCII)
BASE64_DIGEST=re.compile(r"[A-Za-z0-9_-]{43}\Z",re.ASCII)
PROCESS_KEYS=frozenset(("pid","startTicks","uid","gid"))
SNAPSHOT_KEYS=frozenset(("schema","mode","wheels","elfNeeded","loadedElf",
                         "effectiveUnits","processes"))


class VmEvidenceError(ValueError):
    """Never echo archive names, filesystem paths or confidential bytes."""


def denial():
    return {
        "schema":REPORT_SCHEMA,
        "state":"requires-independent-review",
        "wheelRecordObserved":False,
        "externalFactsCompared":False,
        "detachedSignatureVerified":False,
        "osEvidenceAttested":False,
        "dependencyClosureAttested":False,
        "runtimeLibrariesAttested":False,
        "effectiveUnitAttested":False,
        "trustedRootManifestAttested":False,
        "launchAuthorized":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "releaseReady":False,
        "approvedForProduction":False,
        "laboratoryOnly":True,
    }


def _safe_archive_path(value):
    if (type(value) is not str or
            len(value)>255 or not value or
            "\\" in value or "\x00" in value or
            value.startswith("/") or value.endswith("/") or
            "//" in value or any(
                segment in (".","..","") for segment in value.split("/")) or
            str(PurePosixPath(value))!=value):
        raise VmEvidenceError("Wheel contains an unsafe archive entry")


def inspect_wheel_record(wheel_bytes,expected_filename,expected_sha256):
    """Inspect exactly one finite uninstalled wheel archive in memory.

    Verifies entire ZIP SHA, compressed/uncompressed size bounds, no links,
    no unknown/duplicate paths, and PEP 376 RECORD size/hash for every file
    except the RECORD itself. It does not execute/install wheel code.
    """
    if (type(wheel_bytes) is not bytes or
            not 1<=len(wheel_bytes)<=MAX_WHEEL_BYTES or
            type(expected_filename) is not str or
            not sbom.WHL.fullmatch(expected_filename) or
            ".." in expected_filename or
            type(expected_sha256) is not str or
            not HEX_SHA.fullmatch(expected_sha256) or
            not hmac.compare_digest(hashlib.sha256(wheel_bytes).hexdigest(),
                                    expected_sha256)):
        raise VmEvidenceError("Pinned wheel archive missing or has wrong SHA")
    # The first two wheel filename segments are normalized name and version.
    parts=expected_filename[:-4].split("-")
    if len(parts)!=5 or not parts[0] or not parts[1]:
        raise VmEvidenceError("Wheel filename does not match fixed wheel layout")
    dist_info=parts[0]+"-"+parts[1]+".dist-info/"
    record_name=dist_info+"RECORD"
    try:
        with zipfile.ZipFile(io.BytesIO(wheel_bytes),"r") as z:
            members=z.infolist()
            if not 1<=len(members)<=MAX_ZIP_ENTRIES:
                raise VmEvidenceError("Wheel file count exceeded")
            names=[]
            total=0
            for item in members:
                name=item.filename
                _safe_archive_path(name)
                names.append(name)
                if (item.flag_bits & 1 or item.is_dir() or
                        item.compress_type not in (
                            zipfile.ZIP_STORED,zipfile.ZIP_DEFLATED) or
                        (item.create_system==3 and
                         stat.S_IFMT(item.external_attr>>16)
                         not in (0,stat.S_IFREG))):
                    raise VmEvidenceError("Wheel contains unsupported file or link")
                total+=item.file_size
                if item.file_size>MAX_UNCOMPRESSED_BYTES or
                        total>MAX_UNCOMPRESSED_BYTES:
                    raise VmEvidenceError("Wheel decompressed size exceeds limit")
            if len(names)!=len(set(names)) or names.count(record_name)!=1:
                raise VmEvidenceError("Wheel entries or RECORD are duplicated")
            if (dist_info+"WHEEL" not in names or
                    dist_info+"METADATA" not in names):
                raise VmEvidenceError("Wheel mandatory metadata absent")
            file_bytes={}
            for name in names:
                with z.open(name,"r") as stream:
                    body=stream.read(MAX_UNCOMPRESSED_BYTES+1)
                if len(body)!=z.getinfo(name).file_size:
                    raise VmEvidenceError("Wheel file bytes differ from ZIP metadata")
                file_bytes[name]=body
            try:
                record_text=file_bytes[record_name].decode("utf-8",errors="strict")
                rows=list(csv.reader(io.StringIO(record_text,newline=""),
                                     strict=True))
            except (UnicodeError,csv.Error) as exc:
                raise VmEvidenceError("Wheel RECORD is malformed") from exc
            recorded=set()
            for row in rows:
                if len(row)!=3:
                    raise VmEvidenceError("Wheel RECORD row must have 3 columns")
                name,dig,size=row
                _safe_archive_path(name)
                if name not in file_bytes or name in recorded:
                    raise VmEvidenceError("Wheel RECORD has missing or repeated entry")
                recorded.add(name)
                if name==record_name:
                    if dig or size:
                        raise VmEvidenceError("Wheel RECORD must not self-hash")
                    continue
                if not dig.startswith("sha256=") or not BASE64_DIGEST.fullmatch(
                        dig[len("sha256="):]) or
                        not size.isdecimal() or str(int(size))!=size:
                    raise VmEvidenceError("Wheel RECORD lacks strict SHA/size")
                expected=base64.urlsafe_b64encode(
                    hashlib.sha256(file_bytes[name]).digest()).rstrip(b"=").decode()
                if (not hmac.compare_digest(expected,dig[len("sha256="):]) or
                        len(file_bytes[name])!=int(size)):
                    raise VmEvidenceError("Wheel RECORD content digest mismatch")
            if recorded!=set(names):
                raise VmEvidenceError("Wheel has unlisted ZIP content")
    except (zipfile.BadZipFile,RuntimeError,EOFError,OSError,
            OverflowError,MemoryError) as exc:
        raise VmEvidenceError("Wheel archive cannot be safely inspected") from exc
    return {**denial(),"wheelRecordObserved":True}


def _validate_processes(value):
    if (type(value) is not dict or set(value)!={"broker","worker"} or
            any(type(v) is not dict or set(v)!=PROCESS_KEYS
                for v in value.values())):
        raise VmEvidenceError("Process origin facts incomplete")
    for role,profile in value.items():
        if any(type(v) is not int or v<=0 for v in profile.values()):
            raise VmEvidenceError("Untrusted process identity pin")
        if (profile["pid"]>=2**31 or profile["uid"]>=2**31 or
                profile["gid"]>=2**31):
            raise VmEvidenceError("Unbounded process identity")
    if (value["broker"]["pid"]==value["worker"]["pid"] or
            value["broker"]["uid"]==value["worker"]["uid"]):
        raise VmEvidenceError("Worker and broker must be distinct OS identities")


def inspect_external_snapshot(catalog,snapshot,*,expected_processes):
    """Strict comparison of caller-supplied, NOT independently trusted facts."""
    try:
        c=sbom.validate_catalog(catalog)
    except sbom.EvidenceError as exc:
        raise VmEvidenceError("Signed catalog structure invalid") from exc
    _validate_processes(expected_processes)
    if (type(snapshot) is not dict or set(snapshot)!=SNAPSHOT_KEYS or
            snapshot["schema"]!=SNAPSHOT_SCHEMA or
            snapshot["mode"]!="external-offline-observation-untrusted"):
        raise VmEvidenceError("OS snapshot schema is not exact")
    _validate_processes(snapshot["processes"])
    if snapshot["processes"]!=expected_processes:
        raise VmEvidenceError("OS process identity differs from independent pin")
    if (type(snapshot["wheels"]) is not list or
            snapshot["wheels"]!=[w["filename"] for w in c["wheels"]] or
            type(snapshot["elfNeeded"]) is not dict or
            snapshot["elfNeeded"]!=c["elfNeeded"] or
            type(snapshot["loadedElf"]) is not list or
            snapshot["loadedElf"]!=sorted(c["elf"])):
        raise VmEvidenceError("Observed wheel/ELF inventory differs from pinned SBOM")
    try:
        sbom.inspect_effective_unit_snapshot(c,snapshot["effectiveUnits"])
    except sbom.EvidenceError as exc:
        raise VmEvidenceError("Observed systemd properties not strictly denied") from exc
    return {**denial(),"externalFactsCompared":True}


def review_offline_vm_evidence(
        catalog_raw,signature,public_key,*,expected_key_sha256,
        expected_catalog_sha256,expected_generation,legacy_manifest,
        wheel_archives,snapshot,expected_processes):
    """Composite review, never an authorization or trusted OS evidence source.

    Signing key/catalog pins and process pins MUST arrive through a separate
    independently validated operator channel. This function cannot prove
    their provenance, nor the completeness of supplied ELF/systemd snapshots.
    """
    try:
        signed=sbom.inspect_signed_catalog(
            catalog_raw,signature,public_key,
            expected_pubkey_sha256=expected_key_sha256,
            expected_catalog_sha256=expected_catalog_sha256,
            expected_generation=expected_generation,
            legacy_manifest=legacy_manifest)
        catalog=sbom.parse_catalog(catalog_raw)
        if (type(wheel_archives) is not dict or
                set(wheel_archives)!={x["filename"] for x in catalog["wheels"]}):
            raise VmEvidenceError("Wheel archives missing or unexpectedly supplied")
        for item in catalog["wheels"]:
            inspect_wheel_record(wheel_archives[item["filename"]],
                                 item["filename"],item["sha256"])
        inspect_external_snapshot(catalog,snapshot,
                                  expected_processes=expected_processes)
    except sbom.EvidenceError as exc:
        raise VmEvidenceError("Signed offline SBOM review refused") from exc
    return {**denial(),"detachedSignatureVerified":
            signed["detachedSignatureVerified"],
            "wheelRecordObserved":True,
            "externalFactsCompared":True}
