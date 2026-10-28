#!/usr/bin/env python3
"""GD-31J.3: inspect an OFFLINE EuroOffice CEF lab bundle, never launch CEF.

This script cannot authorize document editing or production use. Upstream
CEF disables sandboxing by default unless separately built with CEF_USE_SANDBOX.
A clean Git tree, pinned hash and a local library do NOT prove isolation.
"""
from __future__ import annotations

from dataclasses import dataclass
import argparse
from pathlib import Path
import os
import subprocess
import sys

REQUIRED_COMMITS = {
    "DesktopEditors": "6a671bb1ad238c61965a64de3a005c420940c814",
    "desktop-sdk": "8cc8d747cb48953bf8020c2bfbb67ede6cb55c51",
    "core": "a4a1ba1e9c8e6b6c841c3f059b3a8f82177d63f5",
}


class LabBlocked(RuntimeError):
    """A hard blocking condition; caller must not start or embed CEF."""


@dataclass(frozen=True)
class AssetSet:
    helper: str
    libraries: tuple[str, ...]
    web_editor: str


@dataclass(frozen=True)
class CefLabAssessment:
    lifecycle_assets_present: bool
    document_editing_approved: bool = False
    production_ready: bool = False


def _local_dir(path: Path) -> Path:
    if not path.is_absolute():
        raise LabBlocked("Only fully qualified local paths may be checked.")
    if str(path).startswith("\\\\") or str(path).startswith("//"):
        raise LabBlocked("Network paths and UNC shares are not allowed.")
    path = path.absolute()
    # Resolve each existing component, rejecting symlinks before .resolve().
    cursor = Path(path.anchor)
    for part in path.parts[1:]:
        cursor /= part
        if cursor.is_symlink():
            raise LabBlocked("Links/reparse points are forbidden in the lab path.")
    if not path.is_dir():
        raise LabBlocked("Approved local CEF bundle directory does not exist.")
    return path


def git_head(folder: Path) -> str:
    try:
        result = subprocess.run(
            ["git", "-C", str(folder), "rev-parse", "HEAD"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True,
            text=True, timeout=10)
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as ex:
        raise LabBlocked("Pinned Git revision cannot be established.") from ex
    return result.stdout.strip()


def check_revision_pins(root: Path) -> None:
    super_repo = _local_dir(root)
    targets = {
        "DesktopEditors": super_repo,
        "desktop-sdk": super_repo / "desktop-sdk",
        "core": super_repo / "core",
    }
    for name, folder in targets.items():
        _local_dir(folder)
        if git_head(folder) != REQUIRED_COMMITS[name]:
            raise LabBlocked("Unreviewed " + name + " commit; stop CEF lab.")


def check_assets(root: Path, assets: AssetSet) -> CefLabAssessment:
    root = _local_dir(root)
    entries = [assets.helper, *assets.libraries, assets.web_editor]
    for name in entries:
        relative = Path(name)
        if not name or relative.is_absolute() or ".." in relative.parts:
            raise LabBlocked("Unsafe relative asset name.")
        asset = root / relative
        if not asset.is_file() or asset.is_symlink():
            raise LabBlocked("Missing or unsafe CEF asset: " + name)
        if asset.stat().st_size <= 0:
            raise LabBlocked("Empty CEF asset: " + name)
    return CefLabAssessment(lifecycle_assets_present=True)


def check_lab(root: Path, *, isolation_confirmed: bool,
              allow_documents: bool) -> None:
    if allow_documents:
        raise LabBlocked(
            "CEF document editing remains disabled: sandbox and SDK integration not attested.")
    if not isolation_confirmed:
        raise LabBlocked("A dedicated isolated test machine must be confirmed first.")
    _local_dir(root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="GD-31J.3 offline CEF source and runtime preflight")
    parser.add_argument("--source", type=Path, required=True,
                        help="Approved pinned local DesktopEditors super-repository")
    parser.add_argument("--runtime", type=Path, required=True,
                        help="Approved LOCAL DesktopEditors runtime bundle")
    parser.add_argument("--lab-isolated", action="store_true",
                        help="Operator confirms separate offline test host without sensitive documents")
    parser.add_argument("--enable-documents", action="store_true",
                        help="Explicitly rejected until full sandbox and WORM attestation")
    args = parser.parse_args(argv)
    try:
        check_lab(args.runtime, isolation_confirmed=args.lab_isolated,
                  allow_documents=args.enable_documents)
        check_revision_pins(args.source)
        names = AssetSet(
            helper="editors_helper.exe" if os.name == "nt" else "editors_helper",
            libraries=("libcef.dll",) if os.name == "nt" else ("libcef.so",),
            web_editor="editors/web-apps/apps/api/documents/index.html")
        assessment = check_assets(args.runtime, names)
        if not assessment.lifecycle_assets_present:
            raise LabBlocked("CEF runtime incomplete")
        print("CEF lifecycle assets inspected. NO editor access; NO runtime launched.")
        print("Sandbox and package checks remain manual. Production approved: NO.")
        return 0
    except (LabBlocked, OSError) as ex:
        print("CEF LAB BLOCKED:", str(ex), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
