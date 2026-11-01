#!/usr/bin/env python3
"""GD31J27g: READ-ONLY inventory of crash-leftover native synthetic scratch.

Never deletes. A matching prefix by itself is NOT evidence of ownership.
Requires private owner marker and exact approved synthetic basename set.
No user paths, WORM or unknown documents are opened for their contents.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import json
import os
from pathlib import Path
import re
import stat
import time

import synthetic_roundtrip as ooxml

MAX_AGE = 24 * 3600
MAX_SCRATCH = 64
MAX_CHILDREN = 4
MAX_CHILD_BYTES = 32 * 1024 * 1024
PRIVATE_MARKER = "gd31j27g-private-owner.marker"
PRIVATE_TEXT = b"GETDOC_GD31J27G_PRIVATE_SCRATCH_ONLY\n"
PREFIX = re.compile(r"gd31j26-private-[A-Za-z0-9]{6}\Z", re.ASCII)


class InventoryError(ValueError):
    """Strict synthetic-only read-only refusal. Never disclose unknown filenames."""


def _marker_bytes(path: Path, *, limit=128):
    fd = -1
    try:
        fd = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) |
                     getattr(os, "O_NOFOLLOW", 0))
        st = os.fstat(fd)
        if not stat.S_ISREG(st.st_mode) or st.st_nlink != 1 or st.st_size > limit:
            raise InventoryError("Invalid synthetic lab marker")
        return os.read(fd, limit + 1)
    except OSError as exc:
        raise InventoryError("Cannot read synthetic lab marker") from exc
    finally:
        if fd != -1:
            os.close(fd)


def _old_private_directory(path: Path, now: float, min_age: int):
    st = path.lstat()
    if not stat.S_ISDIR(st.st_mode) or st.st_mode & 0o022:
        return False
    try:
        entries = list(os.scandir(path))
    except OSError:
        return False
    if len(entries) > MAX_CHILDREN or not entries:
        return False
    names = {e.name for e in entries}
    if PRIVATE_MARKER not in names:
        return False
    sources = names.intersection(ooxml.FIXTURES)
    if len(sources) > 1:
        return False
    allowed = {PRIVATE_MARKER}
    if sources:
        fixture = next(iter(sources))
        allowed.add(fixture)
        allowed.add(ooxml.OUTPUTS[fixture])
    if names - allowed:
        return False
    newest = st.st_mtime
    for entry in entries:
        try:
            file_state = entry.stat(follow_symlinks=False)
        except OSError:
            return False
        if (not stat.S_ISREG(file_state.st_mode) or
                file_state.st_nlink != 1 or file_state.st_mode & 0o022 or
                file_state.st_size <= 0 or file_state.st_size > MAX_CHILD_BYTES):
            return False
        newest = max(newest, file_state.st_mtime)
    if now - newest < min_age:
        return False
    try:
        return _marker_bytes(path / PRIVATE_MARKER) == PRIVATE_TEXT
    except InventoryError:
        return False


def scan(staging: Path | str, *, now: float | None = None,
         min_age_seconds: int = MAX_AGE):
    if (type(min_age_seconds) is not int or
            min_age_seconds < MAX_AGE or min_age_seconds > 365 * 24 * 3600):
        raise InventoryError("Minimum orphan age must be at least 24h")
    root = Path(staging)
    if (not root.is_absolute() or str(root) != os.path.normpath(str(root)) or
            str(root).startswith("//")):
        raise InventoryError("Requires canonical local absolute synthetic staging")
    try:
        ooxml._must_be_unlinked_local(root, directory=True)
        output = root / "gd31j25-output"
        ooxml._must_be_unlinked_local(output, directory=True)
        if (output.stat().st_mode & 0o022 or
                _marker_bytes(output / ooxml.OUTPUT_MARKER) !=
                ooxml.OUTPUT_MARKER_TEXT):
            raise InventoryError("Unapproved synthetic output directory")
        found = list(os.scandir(output))
    except (OSError, ooxml.LabError) as exc:
        raise InventoryError("Synthetic staging cannot be inspected safely") from exc
    if len(found) > 512:
        raise InventoryError("Too many entries in approved output directory")
    if now is None:
        now = time.time()
    if not isinstance(now, (float, int)) or not 0 <= now < 1e15:
        raise InventoryError("Invalid inspection clock")
    names, rejected = [], 0
    candidates = [entry for entry in found if entry.name.startswith("gd31j26-private-")]
    if len(candidates) > MAX_SCRATCH:
        raise InventoryError("Scratch directory budget exceeded")
    for entry in candidates:
        if (PREFIX.fullmatch(entry.name) and
                _old_private_directory(output / entry.name, now, min_age_seconds)):
            names.append(entry.name)
        else:
            rejected += 1
    names.sort()
    return {
        "schema": "getdoc-gd31j27g-synthetic-orphan-inventory-lab/1",
        "eligibleForManualReview": len(names),
        "scratchNames": names,
        "rejected": rejected,
        "safeToDeleteAutomatically": False,
        "laboratoryOnly": True,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description="READ-ONLY synthetic native SaveAs leftovers")
    parser.add_argument("--staging", required=True, type=Path)
    parser.add_argument("--minimum-age-hours", type=int, default=24)
    args = parser.parse_args(argv)
    try:
        report = scan(args.staging, min_age_seconds=args.minimum_age_hours * 3600)
    except InventoryError as exc:
        print("GD31J27G_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(report, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
