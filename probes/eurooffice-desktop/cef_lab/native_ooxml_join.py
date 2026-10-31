#!/usr/bin/env python3
"""GD-31J.27: READ-ONLY correlation of *untrusted* native and OOXML evidence.

Input is ONE bounded native stdout receipt via stdin, *never* executable
instructions. Re-check the actual synthetic ZIP bytes using GD-31J.25.
No CEF launch, no file creation, no network, no customer/WORM/SMB access.
A join proves only consistency of claimed event flags and visible canary,
NOT origin/authenticity of the native receipt or production edit capability.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

import synthetic_roundtrip as ooxml

NATIVE_FIELDS = frozenset({
    "schema", "sessionId", "tabId", "fixtureName",
    "nativeSaveRequestObserved", "savePathProvided", "nativeOnSaveAfterAnswer",
    "saveCancelledAfterAnswer", "sourceUnchanged",
    "publishedToDisposableOutput", "outputSha256",
    "nativeSaveProven", "documentVerified", "saveVerified", "pdfVerified",
    "printApproved", "approvedForProduction", "laboratoryOnly",
})
NATIVE_SCHEMA = "getdoc-native-saveas-candidate-lab/1"
JOIN_SCHEMA = "getdoc-native-ooxml-joined-candidate-lab/1"
MAX_NATIVE_BYTES = 2048
SHA_RE = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)
GUID_RE = re.compile(r"[0-9a-f]{32}\Z", re.ASCII)


class JoinError(ValueError):
    """Fail closed without echoing source files or document text."""


def _unique_object(pairs: list[tuple[str, object]]) -> dict:
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise JoinError("Duplicate native JSON key")
        obj[key] = value
    return obj


def _guid(value: str) -> bool:
    return isinstance(value, str) and GUID_RE.fullmatch(value) is not None and int(value, 16) != 0


def _receipt(native_json: str, fixture_name: str,
             expected_session: str, expected_tab: str) -> dict:
    if (not isinstance(native_json, str) or not (2 <= len(native_json) <= MAX_NATIVE_BYTES)
            or any(ord(ch) < 0x20 or ord(ch) > 0x7e for ch in native_json)):
        raise JoinError("Native SaveAs receipt must be bounded single-line ASCII JSON")
    try:
        receipt = json.loads(
            native_json, object_pairs_hook=_unique_object,
            parse_constant=lambda _: (_ for _ in ()).throw(JoinError("Nonfinite native JSON")))
    except (UnicodeError, json.JSONDecodeError, TypeError, RecursionError) as exc:
        raise JoinError("Malformed native JSON receipt") from exc

    if (not isinstance(receipt, dict) or set(receipt) != NATIVE_FIELDS
            or receipt["schema"] != NATIVE_SCHEMA
            or receipt["fixtureName"] != fixture_name
            or receipt["sessionId"] != expected_session
            or receipt["tabId"] != expected_tab):
        raise JoinError("Native schema/session/tab/fixture mismatch")

    for flag in NATIVE_FIELDS - {
        "schema", "sessionId", "tabId", "fixtureName", "outputSha256"
    }:
        if type(receipt[flag]) is not bool:
            raise JoinError("Invalid native boolean field")

    if (not receipt["nativeSaveRequestObserved"]
            or not receipt["savePathProvided"]
            or not receipt["nativeOnSaveAfterAnswer"]
            or receipt["saveCancelledAfterAnswer"]
            or not receipt["sourceUnchanged"]
            or not receipt["publishedToDisposableOutput"]
            or receipt["nativeSaveProven"]
            or receipt["documentVerified"]
            or receipt["saveVerified"]
            or receipt["pdfVerified"]
            or receipt["printApproved"]
            or receipt["approvedForProduction"]
            or not receipt["laboratoryOnly"]):
        raise JoinError("Native SaveAs report does not establish an eligible synthetic candidate")
    sha = receipt["outputSha256"]
    if not isinstance(sha, str) or SHA_RE.fullmatch(sha) is None:
        raise JoinError("Missing or invalid native output digest")
    return receipt


def correlate(staging: Path | str, fixture_name: str, session: str,
              tab: str, native_json: str) -> dict:
    if (fixture_name not in ooxml.FIXTURES or not _guid(session) or
            not _guid(tab) or session == tab):
        raise JoinError("Only approved synthetic fixture and distinct GUID32 session/tab")
    native = _receipt(native_json, fixture_name, session, tab)
    try:
        independent = ooxml.verify(staging, fixture_name)
    except (ooxml.LabError, OSError) as exc:
        raise JoinError("Independent OOXML verification refused output") from exc

    native_sha = native["outputSha256"]
    if (independent["outputSha256"] != native_sha
            or not independent["syntheticPackageVerified"]
            or independent["nativeSaveProven"]
            or independent["documentVerified"]
            or independent["approvedForProduction"]):
        raise JoinError("Native receipt digest differs from independent OOXML output")

    # Another read covers the period between the independent checker and
    # receipt emission. This does not defeat an attacker controlling the VM.
    try:
        output_path = (Path(staging) / "gd31j25-output" /
                       ooxml.OUTPUTS[fixture_name])
        last_digest = hashlib.sha256(
            ooxml._read_limited(output_path, ooxml.MAX_FILE)).hexdigest()
        if last_digest != native_sha:
            raise JoinError("Synthetic output changed during correlation")
        source_path = Path(staging) / fixture_name
        if (hashlib.sha256(ooxml._read_limited(source_path, ooxml.MAX_FILE)).hexdigest()
                != independent["sourceSha256"]):
            raise JoinError("Original synthetic fixture changed during correlation")
    except (ooxml.LabError, OSError) as exc:
        raise JoinError("Final read-only file attestation failed") from exc

    return {
        "schema": JOIN_SCHEMA,
        "sessionId": session,
        "tabId": tab,
        "fixtureName": fixture_name,
        "sourceSha256": independent["sourceSha256"],
        "outputSha256": native_sha,
        "nativeEventChainSelfReported": True,
        "independentPackageVerified": True,
        "syntheticCanaryVerified": True,
        "outputDigestCorrelated": True,
        "sourceUnchanged": True,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }


def correlate_from_stream(staging: Path | str, fixture_name: str,
                          session: str, tab: str, stream) -> dict:
    # One newline-terminated ASCII receipt, nothing before or after.
    # Not a general JSONL command protocol; no streaming of documents.
    data = stream.read(MAX_NATIVE_BYTES + 2)
    if not isinstance(data, bytes) or not (3 <= len(data) <= MAX_NATIVE_BYTES + 1):
        raise JoinError("Truncated or oversized native stdout receipt")
    if data.count(b"\n") != 1 or not data.endswith(b"\n") or b"\r" in data:
        raise JoinError("Native stdout must be one terminated JSON line")
    try:
        payload = data[:-1].decode("ascii", "strict")
    except UnicodeError as exc:
        raise JoinError("Non-ASCII native receipt") from exc
    return correlate(staging, fixture_name, session, tab, payload)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="READ-ONLY lab join: native receipt on STDIN + actual synthetic OOXML")
    parser.add_argument("--staging", required=True, type=Path)
    parser.add_argument("--fixture", required=True, choices=ooxml.FIXTURES)
    parser.add_argument("--session", required=True)
    parser.add_argument("--tab", required=True)
    args = parser.parse_args(argv)
    try:
        result = correlate_from_stream(
            args.staging, args.fixture, args.session, args.tab, sys.stdin.buffer)
    except JoinError as exc:
        print("GD31J27_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(result, separators=(",", ":"), sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
