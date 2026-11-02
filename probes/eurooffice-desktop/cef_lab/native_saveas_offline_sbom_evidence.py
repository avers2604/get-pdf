#!/usr/bin/env python3
"""GD31J27ab: strict offline signed dependency/ELF and systemd observations.

Entirely unwired. No main(), OS launch, cryptographic key creation, filesystem
read, subprocess, network, Linux service inspection, or document publishing.
A detached Ed25519 signature is actually verified only when the optional
cryptography backend is installed in an independently vetted offline image.

Crucial boundary: independently trusted public key fingerprint, expected
catalog digest, monotonic generation, reproducible package provenance,
Python import closure, ELF dynamic loader coverage and effective systemd
properties MUST all be supplied/approved by a separate OS/operator channel.
This module never confers full attestation, launch or SaveAs permission.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import re

import native_saveas_dependency_unit_pins as previous

SCHEMA="getdoc-gd31j27ab-detached-ed25519-offline-sbom-review/1"
RESULT_SCHEMA="getdoc-gd31j27ab-signed-sbom-systemd-denied/1"
CLOSURE_SCOPE="declared-files-only"
MAX_CATALOG_BYTES=32768
MAX_WHEELS=32
SHA=re.compile(r"[0-9a-f]{64}\Z",re.ASCII)
NAME=re.compile(r"[a-z][a-z0-9_-]{1,63}\Z",re.ASCII)
VERSION=re.compile(r"[0-9][0-9A-Za-z.+_-]{0,47}\Z",re.ASCII)
WHL=re.compile(r"[A-Za-z0-9][A-Za-z0-9_.+-]{1,140}\.whl\Z",re.ASCII)
SONAME=re.compile(r"[A-Za-z0-9_+.-]{1,90}\.so(?:\.[0-9]{1,4}){0,3}\Z",re.ASCII)
FIELDS=frozenset(("schema","generation","mode","laboratoryOnly",
                  "publishEnabled","modules","wheels","elf","elfNeeded",
                  "importEdges","units"))
WHEEL_FIELDS=frozenset(("name","version","filename","sha256"))


class EvidenceError(ValueError):
    """Never expose manifest bytes, credentials or private environment."""


def _pairs(pairs):
    obj={}
    for key,value in pairs:
        if key in obj:
            raise EvidenceError("Duplicate signed catalog key")
        obj[key]=value
    return obj


def _sha(value):
    return type(value) is str and SHA.fullmatch(value) and value!="0"*64


def _canonical(value):
    try:
        return (json.dumps(value,sort_keys=True,separators=(",",":"),
                           ensure_ascii=True,allow_nan=False)+"\n").encode("ascii")
    except (ValueError,TypeError,UnicodeError,RecursionError) as exc:
        raise EvidenceError("Signed catalog is not canonical JSON") from exc


def validate_catalog(value):
    if (type(value) is not dict or set(value)!=FIELDS or
            value["schema"]!=SCHEMA or
            type(value["generation"]) is not int or
            not 1<=value["generation"]<2**31 or
            value["mode"]!="inspect_only" or
            value["laboratoryOnly"] is not True or
            value["publishEnabled"] is not False or
            type(value["modules"]) is not dict or
            set(value["modules"])!=previous.MODULE_NAMES or
            any(not _sha(v) for v in value["modules"].values()) or
            type(value["units"]) is not dict or
            set(value["units"])!=set(previous.UNIT_PATHS) or
            any(not _sha(v) for v in value["units"].values()) or
            type(value["elf"]) is not dict or
            not 1<=len(value["elf"])<=16 or
            any(not previous._library_name(k) or not _sha(v)
                for k,v in value["elf"].items())):
        raise EvidenceError("Unsafe signed offline SBOM schema or pins")
    wheels=value["wheels"]
    if type(wheels) is not list or len(wheels)>MAX_WHEELS:
        raise EvidenceError("Oversized wheel inventory")
    filenames=[]
    for item in wheels:
        if (type(item) is not dict or set(item)!=WHEEL_FIELDS or
                type(item["name"]) is not str or
                not NAME.fullmatch(item["name"]) or
                type(item["version"]) is not str or
                not VERSION.fullmatch(item["version"]) or
                type(item["filename"]) is not str or
                not WHL.fullmatch(item["filename"]) or
                ".." in item["filename"] or
                not _sha(item["sha256"])):
            raise EvidenceError("Unpinned or unsafe offline wheel metadata")
        filenames.append(item["filename"])
    if filenames!=sorted(set(filenames)):
        raise EvidenceError("Wheel inventory is unsorted or duplicated")
    imports=value["importEdges"]
    if type(imports) is not dict or set(imports)!=previous.MODULE_NAMES:
        raise EvidenceError("Unpinned local import graph roles")
    for parent,children in imports.items():
        if (type(children) is not list or
                any(type(child) is not str or
                    child not in previous.MODULE_NAMES for child in children) or
                children!=sorted(set(children))):
            raise EvidenceError("Unknown or repeated local import edge")
    needed=value["elfNeeded"]
    if type(needed) is not dict or set(needed)!=set(value["elf"]):
        raise EvidenceError("Missing or additional ELF DT_NEEDED facts")
    known_sonames={path.rsplit("/",1)[1] for path in value["elf"]}
    for parent,children in needed.items():
        if (type(children) is not list or
                any(type(child) is not str or
                    not SONAME.fullmatch(child) or
                    child not in known_sonames for child in children) or
                children!=sorted(set(children))):
            raise EvidenceError("ELF dependency not in pinned shared object list")
    return value


def parse_catalog(raw):
    if (type(raw) is not bytes or
            not 4<=len(raw)<=MAX_CATALOG_BYTES or
            raw.count(b"\n")!=1 or
            not raw.endswith(b"\n") or b"\r" in raw):
        raise EvidenceError("Signed SBOM packet exceeds canonical bounds")
    try:
        value=json.loads(raw[:-1].decode("ascii"),
                         object_pairs_hook=_pairs,
                         parse_constant=lambda _: (_ for _ in ()).throw(
                             EvidenceError("Nonfinite signed catalog")))
    except (ValueError,UnicodeError,RecursionError) as exc:
        if isinstance(exc,EvidenceError):
            raise
        raise EvidenceError("Invalid signed offline catalog JSON") from exc
    validate_catalog(value)
    if _canonical(value)!=raw:
        raise EvidenceError("Signed SBOM bytes not unique canonical JSON")
    return value


def denial():
    return {
        "schema":RESULT_SCHEMA,"state":"requires-independent-review",
        "detachedSignatureVerified":False,
        "pinnedFilesObserved":False,
        "effectiveUnitFactsObserved":False,
        "signatureAttested":False,
        "trustedRootManifestAttested":False,
        "dependencyClosureAttested":False,
        "runtimeLibrariesAttested":False,
        "effectiveUnitAttested":False,
        "launchAuthorized":False,
        "nativePublicationAuthorized":False,
        "writePermissionGranted":False,
        "releaseReady":False,
        "approvedForProduction":False,
        "laboratoryOnly":True,
    }


def _load_ed25519_backend():
    """No network/bootstrap; missing optional cryptography means hard deny."""
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
    return Ed25519PublicKey


def _verify_ed25519(public_key,signature,raw):
    try:
        cls=_load_ed25519_backend()
        cls.from_public_bytes(public_key).verify(signature,raw)
    except Exception as exc:
        raise EvidenceError("Detached Ed25519 verification unavailable or invalid") from exc


def compare_legacy_pins(catalog,legacy_manifest):
    current=validate_catalog(catalog)
    try:
        old=previous.validate_manifest(legacy_manifest)
    except previous.DependencyError as exc:
        raise EvidenceError("Previous operator fixed manifest rejected") from exc
    if (current["modules"]!=old["modules"] or
            current["units"]!=old["units"] or
            current["elf"]!=old["sharedObjects"]):
        raise EvidenceError("Signed SBOM conflicts with previous root-owned pins")
    return False


def inspect_signed_catalog(raw,signature,public_key,*,
                           expected_pubkey_sha256,
                           expected_catalog_sha256,
                           expected_generation,legacy_manifest):
    """Necessary detached signature verification with external trust pins.

    The public key fingerprint and exact catalog fingerprint are provided
    from an INDEPENDENT operator channel. Passing user-controlled pins does
    not create a trust anchor and does NOT approve a production launcher.
    """
    if (type(signature) is not bytes or len(signature)!=64 or
            type(public_key) is not bytes or len(public_key)!=32 or
            not _sha(expected_pubkey_sha256) or
            not _sha(expected_catalog_sha256) or
            type(expected_generation) is not int or
            not 1<=expected_generation<2**31):
        raise EvidenceError("Missing independent signature trust inputs")
    document=parse_catalog(raw)
    if (document["generation"]!=expected_generation or
            not hmac.compare_digest(hashlib.sha256(public_key).hexdigest(),
                                    expected_pubkey_sha256) or
            not hmac.compare_digest(hashlib.sha256(raw).hexdigest(),
                                    expected_catalog_sha256)):
        raise EvidenceError("Operator-pinned SBOM identity or generation mismatch")
    compare_legacy_pins(document,legacy_manifest)
    _verify_ed25519(public_key,signature,raw)
    return {**denial(),"detachedSignatureVerified":True}


def expected_effective_unit(role):
    """Blocked-only policy expectations; NOT a running systemd unit config."""
    if role not in previous.UNIT_PATHS:
        raise EvidenceError("Unrecognized systemd role")
    return {
        "FragmentPath":previous.UNIT_PATHS[role],
        "DropInPaths":"",
        "UnitFileState":"disabled",
        "LoadState":"loaded",
        "ExecStart":"/usr/bin/false",
        "User":previous.UNIT_USERS[role],
        "Group":previous.IPC_GROUP,
        "NoNewPrivileges":"yes",
        "PrivateNetwork":"yes",
        "PrivateMounts":"yes",
        "ProtectSystem":"strict",
        "ProtectHome":"yes",
        "RestrictSUIDSGID":"yes",
        "CapabilityBoundingSet":"",
        "AmbientCapabilities":"",
    }


def inspect_effective_unit_snapshot(catalog,snapshot):
    """Validate isolated externally observed effective systemd properties.

    The exact shape is a review contract; it does not query systemctl,
    D-Bus, drop-ins, cgroup authority, actual seccomp BPF or Astra MAC.
    """
    validate_catalog(catalog)
    if (type(snapshot) is not dict or
            set(snapshot)!=set(previous.UNIT_PATHS)):
        raise EvidenceError("Missing or unexpected service observations")
    for role in previous.UNIT_PATHS:
        if (type(snapshot[role]) is not dict or
                snapshot[role]!=expected_effective_unit(role)):
            raise EvidenceError("Effective systemd state outside blocked allowlist")
    return {**denial(),"effectiveUnitFactsObserved":True}


def compare_inventory(catalog,wheels,elf_mappings):
    """Require exact declared offline files; never assert dependency closure."""
    value=validate_catalog(catalog)
    expected_wheels=tuple(w["filename"] for w in value["wheels"])
    expected_elf=tuple(sorted(value["elf"]))
    if (type(wheels) not in (list,tuple) or
            type(elf_mappings) not in (list,tuple) or
            any(type(x) is not str for x in (*wheels,*elf_mappings)) or
            tuple(wheels)!=expected_wheels or
            tuple(elf_mappings)!=expected_elf):
        raise EvidenceError("Unlisted wheel or dynamic ELF library in observation")
    return {**denial(),"pinnedFilesObserved":True}
