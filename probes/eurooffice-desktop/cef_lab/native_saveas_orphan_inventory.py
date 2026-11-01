#!/usr/bin/env python3
"""GD31J27h: READ-ONLY crash-state reconciliation of synthetic SaveAs scratch.

A post-link hardlink to the *exact approved final output* is not an orphan to
delete. It is an AMBIGUOUS published result requiring manual review. No delete,
restore, writeback, chmod, rename or auto-cleanup operations exist here.
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
SCHEMA = "getdoc-gd31j27h-synthetic-orphan-reconciled-lab/2"
DIR_FLAGS = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | \
    getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)


class InventoryError(ValueError):
    """Strict synthetic-only read-only refusal; never expose foreign filenames."""


def _marker_bytes(name, *, dir_fd, limit=128):
    fd = -1
    try:
        fd = os.open(name, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) |
                     getattr(os, "O_NOFOLLOW", 0), dir_fd=dir_fd)
        first = os.fstat(fd)
        if (not stat.S_ISREG(first.st_mode) or first.st_nlink != 1 or
                first.st_mode & 0o022 or first.st_size <= 0 or
                first.st_size > limit):
            raise InventoryError("Invalid synthetic lab marker")
        raw = os.read(fd, limit + 1)
        last = os.fstat(fd)
        if (len(raw) != first.st_size or first.st_ino != last.st_ino or
                first.st_size != last.st_size or
                first.st_ctime_ns != last.st_ctime_ns or
                first.st_mtime_ns != last.st_mtime_ns):
            raise InventoryError("Synthetic marker changed during review")
        return raw
    except OSError as exc:
        raise InventoryError("Cannot read synthetic lab marker") from exc
    finally:
        if fd != -1:
            os.close(fd)


def _old_private_directory(path: Path, output_fd: int,
                           now: float, min_age: int):
    """Return 'private_only' or 'linked_final'; None means reject, not delete."""
    fd = -1
    try:
        before = path.lstat()
        if not stat.S_ISDIR(before.st_mode) or before.st_mode & 0o022:
            return None
        fd = os.open(path, DIR_FLAGS)
        current = os.fstat(fd)
        if (not stat.S_ISDIR(current.st_mode) or current.st_mode & 0o022 or
                (before.st_dev, before.st_ino) !=
                (current.st_dev, current.st_ino)):
            return None
        with os.scandir(fd) as scanner:
            entries = list(scanner)
        if not 1 <= len(entries) <= MAX_CHILDREN:
            return None
        names = {entry.name for entry in entries}
        sources = names.intersection(ooxml.FIXTURES)
        if PRIVATE_MARKER not in names or len(sources) > 1:
            return None
        allowed = {PRIVATE_MARKER}
        saved_name = None
        if sources:
            fixture = next(iter(sources))
            saved_name = ooxml.OUTPUTS[fixture]
            allowed.update((fixture, saved_name))
        if names - allowed:
            return None
        newest = max(current.st_mtime, current.st_ctime)
        saved_state = None
        for name in names:
            metadata = os.stat(name, dir_fd=fd, follow_symlinks=False)
            if (not stat.S_ISREG(metadata.st_mode) or
                    metadata.st_mode & 0o022 or
                    metadata.st_size <= 0 or metadata.st_size > MAX_CHILD_BYTES):
                return None
            if name == saved_name:
                if metadata.st_nlink not in (1, 2):
                    return None
                saved_state = metadata
            elif metadata.st_nlink != 1:
                return None
            newest = max(newest, metadata.st_mtime, metadata.st_ctime)
        if now - newest < min_age:
            return None
        if _marker_bytes(PRIVATE_MARKER, dir_fd=fd) != PRIVATE_TEXT:
            return None

        # Any preexisting final name needs a proven inode match, otherwise
        # treat as unrecognized: it might belong to another actor.
        if saved_name:
            try:
                final = os.stat(saved_name, dir_fd=output_fd,
                                follow_symlinks=False)
            except FileNotFoundError:
                final = None
            if final is not None:
                if (saved_state is None or saved_state.st_nlink != 2 or
                        not stat.S_ISREG(final.st_mode) or
                        final.st_mode & 0o022 or final.st_nlink != 2 or
                        (final.st_dev, final.st_ino, final.st_size) !=
                        (saved_state.st_dev, saved_state.st_ino,
                         saved_state.st_size)):
                    return None
                return "linked_final"
            if saved_state is not None and saved_state.st_nlink != 1:
                return None
        return "private_only"
    except (OSError, InventoryError):
        return None
    finally:
        if fd != -1:
            os.close(fd)


def scan(staging: Path | str, *, now: float | None = None,
         min_age_seconds: int = MAX_AGE):
    if (type(min_age_seconds) is not int or
            min_age_seconds < MAX_AGE or min_age_seconds > 365 * 24 * 3600):
        raise InventoryError("Minimum orphan age must be at least 24h")
    root = Path(staging)
    if (not root.is_absolute() or str(root) != os.path.normpath(str(root)) or
            str(root).startswith("//")):
        raise InventoryError("Requires canonical local absolute synthetic staging")
    fd = -1
    try:
        ooxml._must_be_unlinked_local(root, directory=True)
        output = root / "gd31j25-output"
        ooxml._must_be_unlinked_local(output, directory=True)
        fd = os.open(output, DIR_FLAGS)
        output_stat = os.fstat(fd)
        if (not stat.S_ISDIR(output_stat.st_mode) or output_stat.st_mode & 0o022 or
                _marker_bytes(ooxml.OUTPUT_MARKER, dir_fd=fd) !=
                ooxml.OUTPUT_MARKER_TEXT):
            raise InventoryError("Unapproved synthetic output directory")
        with os.scandir(fd) as scanner:
            found = list(scanner)
        if len(found) > 512:
            raise InventoryError("Too many entries in approved output directory")
        if now is None:
            now = time.time()
        if type(now) not in (float, int) or not 0 <= now < 1e15:
            raise InventoryError("Invalid inspection clock")
        names, rejected, linked = [], 0, 0
        candidates = [entry.name for entry in found if
                      entry.name.startswith("gd31j26-private-")]
        if len(candidates) > MAX_SCRATCH:
            raise InventoryError("Scratch directory budget exceeded")
        for name in candidates:
            if not PREFIX.fullmatch(name):
                rejected += 1
                continue
            result = _old_private_directory(output / name, fd,
                                            now, min_age_seconds)
            if result:
                names.append(name)
                linked += result == "linked_final"
            else:
                rejected += 1
        names.sort()
        return {
            "schema": SCHEMA,
            "eligibleForManualReview": len(names),
            "linkedPublicationPendingReview": linked,
            "scratchNames": names,
            "rejected": rejected,
            "safeToDeleteAutomatically": False,
            "laboratoryOnly": True,
        }
    except (OSError, ooxml.LabError) as exc:
        raise InventoryError("Synthetic staging cannot be inspected safely") from exc
    finally:
        if fd != -1:
            os.close(fd)


def main(argv=None):
    parser = argparse.ArgumentParser(description="READ-ONLY native SaveAs crash reconciliation")
    parser.add_argument("--staging", required=True, type=Path)
    parser.add_argument("--minimum-age-hours", type=int, default=24)
    args = parser.parse_args(argv)
    try:
        report = scan(args.staging, min_age_seconds=args.minimum_age_hours * 3600)
    except InventoryError as exc:
        print("GD31J27H_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(report, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
