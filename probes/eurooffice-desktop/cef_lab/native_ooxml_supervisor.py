#!/usr/bin/env python3
"""GD-31J.27a: supervised LAB-ONLY native SaveAs process -> OOXML join.

Runs exactly one pinned native host with bounded stdout, without a shell,
then independently checks the synthetic OOXML bytes. No filesystem writes
are performed by this supervisor; the native host's disposable SaveAs is
an explicit, separate lab operation. Running on a disposable offline VM is
an OPERATOR obligation, not something this program can attest or enforce.

The resulting envelope reports observed process mechanics, NOT certified
process identity, sandbox status, real editor provenance or production rights.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True  # Before importing local Python modules.

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import select
import signal
import stat
import subprocess
import time

import native_ooxml_join as join
import synthetic_roundtrip as ooxml

ENVELOPE_SCHEMA = "getdoc-native-ooxml-supervised-candidate-lab/1"
MAX_BINARY_BYTES = 512 * 1024 * 1024
NATIVE_TIMEOUT_SECONDS = 120
SHA_RE = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)


class SupervisorError(ValueError):
    """Fail-closed error without document content, native stdout or secrets."""


def _binary_digest(path: Path) -> str:
    """Hash a regular executable on a local, non-symlink path, without loading it into RAM."""
    try:
        ooxml._must_be_unlinked_local(path)
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        fd = os.open(path, flags)
        with os.fdopen(fd, "rb") as stream:
            first = os.fstat(stream.fileno())
            if (not stat.S_ISREG(first.st_mode) or first.st_size < 1 or
                    first.st_size > MAX_BINARY_BYTES or first.st_mode & 0o022 or
                    not os.access(path, os.X_OK)):
                raise SupervisorError("Native host is not a pinned, non-writable executable")
            digest = hashlib.sha256()
            while True:
                chunk = stream.read(1024 * 1024)
                if not chunk:
                    break
                digest.update(chunk)
            last = os.fstat(stream.fileno())
            if ((first.st_size, first.st_mtime_ns, first.st_ctime_ns, first.st_ino) !=
                    (last.st_size, last.st_mtime_ns, last.st_ctime_ns, last.st_ino)):
                raise SupervisorError("Native host changed while being hashed")
            return digest.hexdigest()
    except (OSError, ooxml.LabError) as exc:
        raise SupervisorError("Native host pin check failed") from exc


def capture_native(argv: list[str], env: dict[str, str],
                   *, max_stdout_bytes: int = join.MAX_NATIVE_BYTES + 1) -> tuple[int, bytes]:
    """Launch one host; bound captured stdout/elapsed time, never invoke shell or read stderr.

    An executable SHA sampled before/after is only evidence. It is NOT a kernel
    guarantee against concurrent privileged binary replacement or injected code.
    """
    if type(max_stdout_bytes) is not int or not 64 <= max_stdout_bytes <= 8192:
        raise SupervisorError("Invalid native stdout bound")
    if not sys.platform.startswith("linux"):
        raise SupervisorError("Native CEF SaveAs lab is supported only on Linux/Wayland")
    process = None
    completed = False
    try:
        process = subprocess.Popen(
            argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, cwd=str(Path(argv[0]).parent),
            env=env, shell=False, start_new_session=True, close_fds=True)
        assert process.stdout is not None
        fd = process.stdout.fileno()
        os.set_blocking(fd, False)
        expiry = time.monotonic() + NATIVE_TIMEOUT_SECONDS
        collected = bytearray()
        eof = False
        while not eof:
            remaining = expiry - time.monotonic()
            if remaining <= 0:
                raise SupervisorError("Native host timed out")
            readable, _, _ = select.select([fd], [], [], min(0.25, remaining))
            if not readable:
                continue
            block = os.read(fd, 4096)
            if not block:
                eof = True
            else:
                collected.extend(block)
                if len(collected) > max_stdout_bytes:
                    raise SupervisorError("Native stdout exceeds the strict receipt budget")
        remaining = expiry - time.monotonic()
        if remaining <= 0:
            raise SupervisorError("Native host timed out before exit")
        status = process.wait(timeout=remaining)
        completed = True
        return status, bytes(collected)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise SupervisorError("Native process capture failed") from exc
    finally:
        if process is not None:
            try:
                if not completed:
                    # A child may exit but leave a helper holding the pipe.
                    # Kill the entire private session on any incomplete capture.
                    os.killpg(process.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                pass
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                pass
            if process.stdout is not None:
                process.stdout.close()


def run_supervised(
    staging: Path | str, fixture_name: str, session: str, tab: str,
    native_binary: Path | str, expected_native_sha256: str,
    *, approved_disposable_vm: bool = False,
    env: dict[str, str] | None = None,
    capture=None,
) -> dict:
    """Deny any attempt not explicitly scoped to a disposable synthetic lab.

    The supplied expected SHA must originate from a separately reviewed pinned
    binary manifest. Supplying an untrusted SHA alongside a binary is not trust.
    """
    root = Path(staging)
    executable = Path(native_binary)
    environment = os.environ.copy() if env is None else dict(env)
    if not approved_disposable_vm:
        raise SupervisorError("Explicit disposable offline VM approval is missing")
    if (fixture_name not in ooxml.FIXTURES or not join._guid(session) or
            not join._guid(tab) or session == tab):
        raise SupervisorError("Unapproved fixture or session/tab")
    if environment.get("QT_QPA_PLATFORM") != "wayland":
        raise SupervisorError("Wayland must be preconfigured by the lab operator")
    if not isinstance(expected_native_sha256, str) or not SHA_RE.fullmatch(expected_native_sha256):
        raise SupervisorError("Invalid pinned binary SHA-256")
    try:
        ooxml._must_be_unlinked_local(root, directory=True)
        output_root = root / "gd31j25-output"
        ooxml._must_be_unlinked_local(output_root, directory=True)
        if (ooxml._read_limited(output_root / ooxml.OUTPUT_MARKER, 1024)
                != ooxml.OUTPUT_MARKER_TEXT):
            raise SupervisorError("Disposable output marker mismatch")
    except ooxml.LabError as exc:
        raise SupervisorError("Invalid disposable source/output staging") from exc
    # Do not let the executable reside inside a staging tree which the native
    # process is expected to write to. The native host is not a trusted writer.
    if not executable.is_absolute() or executable == root or root in executable.parents:
        raise SupervisorError("Native executable must be outside disposable staging")
    output_file = output_root / ooxml.OUTPUTS[fixture_name]
    if os.path.lexists(output_file):
        raise SupervisorError("Synthetic target already exists; refusing overwrite")
    before = _binary_digest(executable)
    if before != expected_native_sha256:
        raise SupervisorError("Native host differs from separately reviewed pin")

    args = [str(executable), "--open-staged-native-saveas-lab",
            str(root), fixture_name, session, tab]
    try:
        code, raw = (capture or capture_native)(args, environment)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise SupervisorError("Native process launch failed") from exc
    if type(code) is not int or code != 0:
        raise SupervisorError("Native SaveAs process did not exit successfully")
    if not isinstance(raw, bytes):
        raise SupervisorError("Native stdout must contain bounded bytes only")
    after = _binary_digest(executable)
    if before != after:
        raise SupervisorError("Native binary changed across the lab run")
    # This checks one complete native line and independently opens OOXML.
    joined = join.correlate_from_stream(root, fixture_name, session, tab, io.BytesIO(raw))
    return {
        "schema": ENVELOPE_SCHEMA,
        "sessionId": session,
        "tabId": tab,
        "fixtureName": fixture_name,
        "nativeBinarySha256": before,
        "nativeProcessExitZero": True,
        "nativeBinaryPinMatched": True,
        "stdoutSingleLineBounded": True,
        "joinedCandidate": joined,
        "nativeSaveProven": False,
        "documentVerified": False,
        "saveVerified": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="PINNED native SaveAs + OOXML lab, NEVER production")
    parser.add_argument("--staging", required=True, type=Path)
    parser.add_argument("--fixture", required=True, choices=ooxml.FIXTURES)
    parser.add_argument("--session", required=True)
    parser.add_argument("--tab", required=True)
    parser.add_argument("--native-binary", required=True, type=Path)
    parser.add_argument("--native-sha256", required=True)
    parser.add_argument("--i-confirm-disposable-offline-vm", action="store_true")
    args = parser.parse_args(argv)
    try:
        evidence = run_supervised(
            args.staging, args.fixture, args.session, args.tab,
            args.native_binary, args.native_sha256,
            approved_disposable_vm=args.i_confirm_disposable_offline_vm)
    except (SupervisorError, join.JoinError) as exc:
        print("GD31J27A_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(evidence, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
