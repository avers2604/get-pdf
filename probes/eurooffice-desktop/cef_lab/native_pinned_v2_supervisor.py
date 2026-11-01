#!/usr/bin/env python3
"""GD-31J.27d: manually approved disposable-VM process supervision for PINNED v2.

No trust in stdout alone. Hash one exact native host and TWO explicit runtime
library files before/after a bounded subprocess launch, then independently
reopen synthetic OOXML. A path/hash pair is NOT a trusted signed manifest,
actual ld.so mapping proof, sandbox, or CEF process attestation.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat

import native_ooxml_supervisor as old
import native_pinned_event_ooxml_join as joined_v2
import native_pinned_saveas_event_v2 as event_v2
import synthetic_roundtrip as ooxml

SCHEMA = "getdoc-native-pinned-v2-supervised-ooxml-lab/1"
ROLES = ("ascdocumentscore", "qtascdocumentscore")
MAX_LIBRARY_BYTES = 512 * 1024 * 1024
# Never inherit untrusted loader injection, arbitrary Qt plugins or network proxies.
ENV_FIELDS = frozenset({
    "QT_QPA_PLATFORM", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR",
    "HOME", "LANG", "LC_ALL", "PATH", "TMPDIR",
})


class V2SupervisorError(ValueError):
    """No native stdout, path, document data, or secrets in an error message."""


def _clean_path(path: Path):
    if (not path.is_absolute() or str(path).startswith("//") or
            str(path) != os.path.normpath(str(path)) or
            "\x00" in str(path)):
        raise V2SupervisorError("Only canonical absolute lab path syntax is permitted")
    try:
        ooxml._must_be_unlinked_local(path)
    except ooxml.LabError as exc:
        raise V2SupervisorError("Linked, missing or nonregular lab artifact") from exc


def _runtime_digest(path: Path):
    _clean_path(path)
    try:
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
        with os.fdopen(os.open(path, flags), "rb") as stream:
            before = os.fstat(stream.fileno())
            if (not stat.S_ISREG(before.st_mode) or not 8 <= before.st_size <= MAX_LIBRARY_BYTES or
                    (before.st_mode & 0o022)):
                raise V2SupervisorError("Native runtime library is not a protected regular file")
            digest = hashlib.sha256()
            first_bytes = stream.read(4)
            if first_bytes != b"\x7fELF":
                raise V2SupervisorError("Pinned Linux runtime library is not ELF")
            digest.update(first_bytes)
            while True:
                chunk = stream.read(1024 * 1024)
                if not chunk:
                    break
                digest.update(chunk)
            after = os.fstat(stream.fileno())
            before_id = (before.st_dev, before.st_ino, before.st_size,
                         before.st_mtime_ns, before.st_ctime_ns)
            after_id = (after.st_dev, after.st_ino, after.st_size,
                        after.st_mtime_ns, after.st_ctime_ns)
            if before_id != after_id:
                raise V2SupervisorError("Native runtime changed during pinned read")
            return digest.hexdigest()
    except OSError as exc:
        raise V2SupervisorError("Pinned library could not be read") from exc


def _validated_runtime_pins(root: Path, libs: dict):
    if type(libs) is not dict or set(libs) != set(ROLES):
        raise V2SupervisorError("Exactly two approved native runtime libraries are required")
    paths = {}
    digests = {}
    for role in ROLES:
        entry = libs[role]
        if type(entry) not in (tuple, list) or len(entry) != 2:
            raise V2SupervisorError("Expected an exact (absolute library path, approved SHA) pair")
        path = Path(entry[0])
        pin = entry[1]
        if (type(pin) is not str or not old.SHA_RE.fullmatch(pin) or
                not re.fullmatch(r"lib" + role + r"\.so(?:\.\d+)*", path.name)):
            raise V2SupervisorError("Wrong library name or reviewed pinned SHA")
        _clean_path(path)
        if root == path or root in path.parents:
            raise V2SupervisorError("Runtime libraries must be outside synthetic staging")
        sha = _runtime_digest(path)
        if sha != pin:
            raise V2SupervisorError("Native runtime library differs from reviewed SHA pin")
        paths[role] = path
        digests[role] = sha
    if paths[ROLES[0]].parent != paths[ROLES[1]].parent:
        raise V2SupervisorError("Both pinned runtime libraries must be in one approved directory")
    return paths, digests


def _environment(raw, libdir):
    env = {key: val for key, val in os.environ.items() if key in ENV_FIELDS} if raw is None else dict(raw)
    if (set(env) - ENV_FIELDS or
            any(type(key) is not str or type(value) is not str or
                "\x00" in value or "\n" in value for key, value in env.items()) or
            env.get("QT_QPA_PLATFORM") != "wayland"):
        raise V2SupervisorError("Only approved Wayland environment variables are allowed")
    env["PATH"] = "/usr/bin:/bin"
    # Do not accept LD_LIBRARY_PATH from user input: fix it to the two pinned
    # runtime libraries' common directory; other ld.so inputs remain unpinned.
    env["LD_LIBRARY_PATH"] = str(libdir)
    return env


def run_supervised_v2(
    staging: Path | str, fixture: str, session: str, tab: str,
    native_binary: Path | str, native_sha256: str,
    runtime_pins: dict,
    *, approved_disposable_vm: bool = False,
    env: dict[str, str] | None = None,
    capture=None,
):
    """Run only exact pinned v2 argv and validate independently, never write a document."""
    root = Path(staging)
    binary = Path(native_binary)
    if not approved_disposable_vm:
        raise V2SupervisorError("Operator has not approved an isolated disposable offline VM")
    if (not root.is_absolute() or str(root) != os.path.normpath(str(root)) or
            not joined_v2.old_join._guid(session) or
            not joined_v2.old_join._guid(tab) or session == tab or
            fixture not in ooxml.FIXTURES):
        raise V2SupervisorError("Wrong staging, session, tab, or approved synthetic fixture")
    try:
        ooxml._must_be_unlinked_local(root, directory=True)
        outputs = root / "gd31j25-output"
        ooxml._must_be_unlinked_local(outputs, directory=True)
        if (outputs.stat().st_mode & 0o022 or
                ooxml._read_limited(outputs / ooxml.OUTPUT_MARKER, 1024) !=
                ooxml.OUTPUT_MARKER_TEXT):
            raise V2SupervisorError("Disposable output marker or permissions invalid")
    except (ooxml.LabError, OSError) as exc:
        raise V2SupervisorError("Synthetic staging cannot be trusted") from exc

    output = outputs / ooxml.OUTPUTS[fixture]
    if os.path.lexists(output):
        raise V2SupervisorError("Approved synthetic output already exists (no overwrite)")
    _clean_path(binary)
    if root == binary or root in binary.parents:
        raise V2SupervisorError("Native binary cannot be placed inside writable synthetic staging")
    if type(native_sha256) is not str or not old.SHA_RE.fullmatch(native_sha256):
        raise V2SupervisorError("Invalid separately approved native host pin")
    try:
        binary_before = old._binary_digest(binary)
    except old.SupervisorError as exc:
        raise V2SupervisorError("Native binary failed pinned read") from exc
    if binary_before != native_sha256:
        raise V2SupervisorError("Native host SHA does not match separately approved pin")

    library_paths, library_hashes = _validated_runtime_pins(root, runtime_pins)
    child_env = _environment(env, library_paths[ROLES[0]].parent)
    argv = [str(binary), "--open-staged-native-saveas-event-v2-lab",
            str(root), fixture, session, tab]

    try:
        if capture is None:
            code, raw = old.capture_native(
                argv, child_env, max_stdout_bytes=event_v2.MAX_BYTES + 1)
        else:
            code, raw = capture(argv, child_env)
    except (old.SupervisorError, OSError) as exc:
        raise V2SupervisorError("Native pinned v2 process capture failed") from exc
    if type(code) is not int or code != 0 or type(raw) is not bytes:
        raise V2SupervisorError("Native pinned v2 process did not return one successful stdout")
    # Re-read ALL immutable pins after child exit, before interpreting stdout.
    try:
        if old._binary_digest(binary) != binary_before:
            raise V2SupervisorError("Native host changed during laboratory attempt")
    except old.SupervisorError as exc:
        raise V2SupervisorError("Native executable pin changed after process") from exc
    for role in ROLES:
        if _runtime_digest(library_paths[role]) != library_hashes[role]:
            raise V2SupervisorError("Native linked runtime changed during laboratory attempt")
    try:
        verified = joined_v2.from_stream(root, fixture, session, tab, io.BytesIO(raw))
    except joined_v2.V2JoinError as exc:
        raise V2SupervisorError("Native stdout or independent OOXML validation refused") from exc

    return {
        "schema": SCHEMA,
        "sessionId": session,
        "tabId": tab,
        "fixtureName": fixture,
        "nativeBinarySha256": binary_before,
        "runtimeLibrarySha256": {role: library_hashes[role] for role in ROLES},
        "nativeProcessExitZero": True,
        "runtimeLibraryPinsMatched": True,
        "stdoutSingleLineBounded": True,
        "joinedCandidate": verified,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description="GD31J27d: pinned v2 SaveAs offline VM LAB only")
    parser.add_argument("--staging", required=True, type=Path)
    parser.add_argument("--fixture", required=True, choices=ooxml.FIXTURES)
    parser.add_argument("--session", required=True)
    parser.add_argument("--tab", required=True)
    parser.add_argument("--native-binary", required=True, type=Path)
    parser.add_argument("--native-sha256", required=True)
    parser.add_argument("--ascdocumentscore", required=True, type=Path)
    parser.add_argument("--ascdocumentscore-sha", required=True)
    parser.add_argument("--qtascdocumentscore", required=True, type=Path)
    parser.add_argument("--qtascdocumentscore-sha", required=True)
    parser.add_argument("--i-confirm-disposable-offline-vm", action="store_true")
    args = parser.parse_args(argv)
    try:
        result = run_supervised_v2(
            args.staging, args.fixture, args.session, args.tab,
            args.native_binary, args.native_sha256,
            {
                "ascdocumentscore": (args.ascdocumentscore, args.ascdocumentscore_sha),
                "qtascdocumentscore": (args.qtascdocumentscore, args.qtascdocumentscore_sha),
            },
            approved_disposable_vm=args.i_confirm_disposable_offline_vm)
    except V2SupervisorError as exc:
        print("GD31J27D_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
