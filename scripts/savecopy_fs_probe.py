#!/usr/bin/env python3
"""GD31J27al: opt-in synthetic filesystem facts, NOT production SaveAs E2E.

Run only in an independently approved disposable OFFLINE VM and a freshly
created, EMPTY, owner-private sandbox directory. No document paths, file
handles, network requests, subprocesses or corporate services are touched.

This probes os.link exclusive-name behavior; it DOES NOT execute .NET
File.Move and must never be cited as proof of its behavior on NTFS/SMB/ext4.
"""
from __future__ import annotations

import errno
import json
import os
from pathlib import Path
import secrets
import stat
import sys

SANDBOX_PREFIX = "getdoc-gd31j27al-disposable-"
ENV_NAME = "GETDOC_GD31J27AL_OFFLINE_VM"
ENV_VALUE = "NO_DOCUMENTS_DISPOSABLE_FS_ONLY"
ACK = "I_APPROVE_SYNTHETIC_DISPOSABLE_FS_PROBE"
_FIXTURE = b"GD31J27AL_SYNTHETIC_BYTES_NOT_A_DOCUMENT\n"
_FOREIGN = b"GD31J27AL_OTHER_SYNTHETIC_BYTES\n"
_UNSUPPORTED = {errno.ENOSYS, errno.EPERM, errno.EACCES, errno.EOPNOTSUPP}
if hasattr(errno, "ENOTSUP"):
    _UNSUPPORTED.add(errno.ENOTSUP)


class ProbeError(ValueError):
    """The offline operator's sandbox is unsafe or evidence is contradictory."""


def _guard(root: Path) -> None:
    if not root.is_absolute() or not root.name.startswith(SANDBOX_PREFIX):
        raise ProbeError("Use a uniquely named absolute disposable sandbox")
    if len(root.name) < len(SANDBOX_PREFIX) + 8:
        raise ProbeError("Use a unique sandbox name")
    # Check every path component without following symlinks/junctions.
    for segment in (root, *root.parents):
        try:
            metadata = segment.lstat()
        except OSError as exc:
            raise ProbeError("Sandbox path unavailable") from exc
        if (not stat.S_ISDIR(metadata.st_mode) or
                stat.S_ISLNK(metadata.st_mode) or
                getattr(segment, "is_junction", lambda: False)()):
            raise ProbeError("Linked or non-directory sandbox path prohibited")
    if os.name != "nt":
        info = root.stat(follow_symlinks=False)
        if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) & 0o022:
            raise ProbeError("Sandbox must be owned by current user and not group/world writable")
    if any(root.iterdir()):
        raise ProbeError("Sandbox must be empty before probe")


def _result(state: str, collision: bool, new_copy: bool) -> dict:
    return {
        "schema": "getdoc-gd31j27al-synthetic-fs-observation/1",
        "state": state,
        "collisionPreserved": collision,
        "newCopyBytesVerified": new_copy,
        "netFileMoveTested": False,
        "nativeSaveProven": False,
        "productionStorageAttested": False,
        "releaseReady": False,
    }


def observe(root: Path) -> dict:
    """Test only a scratch directory the caller created and verified empty.

    os.link has distinct semantics from .NET File.Move; the result is a
    synthetic filesystem capability observation, not a SaveAs certification.
    """
    _guard(root)
    label = secrets.token_hex(12)
    stage = root / (".gd31j27al-stage-" + label + ".bin")
    foreign = root / (".gd31j27al-existing-" + label + ".bin")
    published = root / (".gd31j27al-copy-" + label + ".bin")
    created: list[Path] = []
    try:
        for target, data in ((stage, _FIXTURE), (foreign, _FOREIGN)):
            with target.open("xb") as stream:
                created.append(target)
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
        try:
            os.link(stage, foreign, follow_symlinks=False)
        except FileExistsError:
            pass
        except OSError as exc:
            if exc.errno in _UNSUPPORTED:
                return _result("unsupported_not_attested", False, False)
            raise ProbeError("Exclusive-name collision observation failed") from exc
        else:
            raise ProbeError("Existing sandbox test name was unexpectedly replaced")
        if foreign.read_bytes() != _FOREIGN:
            raise ProbeError("The protected synthetic collision target changed")
        try:
            os.link(stage, published, follow_symlinks=False)
            created.append(published)
        except OSError as exc:
            if exc.errno in _UNSUPPORTED:
                return _result("unsupported_not_attested", False, False)
            raise ProbeError("Fresh-name publication observation failed") from exc
        if published.read_bytes() != _FIXTURE:
            raise ProbeError("The newly linked synthetic bytes differ")
        return _result("observed_not_attested", True, True)
    finally:
        # Only unlink the three exact random names made by THIS invocation;
        # never recursively clean or delete the sandbox root itself.
        for path in reversed(created):
            try:
                path.unlink(missing_ok=True)
            except OSError as exc:
                raise ProbeError("Synthetic cleanup failed; inspect disposable VM") from exc


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if (len(args) != 3 or args[0] != "--sandbox" or args[2] != ACK or
            os.environ.get(ENV_NAME) != ENV_VALUE):
        return 2
    try:
        outcome = observe(Path(args[1]))
    except (ProbeError, OSError, ValueError):
        return 2
    print(json.dumps(outcome, sort_keys=True, separators=(",", ":")))
    return 2  # Even a fully observed synthetic fixture NEVER authorizes SaveAs.


if __name__ == "__main__":
    raise SystemExit(main())
