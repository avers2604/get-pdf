#!/usr/bin/env python3
"""GD-31J.24: explicit lab-only ready-notification overlay for pinned EuroOffice.

Never auto-applies. Requires an acknowledged, separate disposable SDK checkout,
exact upstream commit, clean source and verified textual anchors. Only two
source files are modified in the LAB COPY. This does not certify content ready:
the renderer-originated notification remains untrusted diagnostic telemetry.
"""
from __future__ import annotations

import argparse
import pathlib
import subprocess
import sys

UPSTREAM_PIN = "8cc8d747cb48953bf8020c2bfbb67ede6cb55c51"
APPROVAL_SENTINEL = ".getdoc-gd31j24-disposable-lab"
SENTINEL_TEXT = "GETDOC_DISPOSABLE_OFFLINE_LAB_ONLY\n"
HEADER = pathlib.Path("ChromiumBasedEditors/lib/include/applicationmanager_events.h")
SOURCE = pathlib.Path("ChromiumBasedEditors/lib/src/cefview.cpp")
CHANGED_FILES = (HEADER, SOURCE)
EVENT_NAME = "ASC_MENU_EVENT_TYPE_GETDOC_DOCUMENT_CONTENT_READY"
MARKER_NAME = "GETDOC_GD31J24_READY_EVENT_BRIDGE"

HEADER_ANCHOR = "#define ASC_MENU_EVENT_TYPE_CEF_LOCALFILE_TEMPLATES         9010"
HANDLER_ANCHOR = '''\telse if ("on_document_content_ready" == message_name)
\t{
\t\tif (!m_pParent->m_pInternal->m_sComparingFile.empty())
\t\t{
\t\t\tm_pParent->m_pInternal->m_oConverterToEditor.m_sComparingFile = m_pParent->m_pInternal->m_sComparingFile;
\t\t\tm_pParent->m_pInternal->LocalFile_Start();
\t\t}

\t\treturn true;
\t}'''


class OverlayError(RuntimeError):
    """Unapproved checkout, unexpected SDK version or mismatched source."""


def render_overlay(header: str, source: str) -> tuple[str, str]:
    if (header.count(HEADER_ANCHOR) != 1 or source.count(HANDLER_ANCHOR) != 1 or
            EVENT_NAME in header or EVENT_NAME in source or MARKER_NAME in header):
        raise OverlayError("Pinned upstream anchors are missing, duplicated or already patched")
    header = header.replace(HEADER_ANCHOR, HEADER_ANCHOR + "\n" +
        "#define " + EVENT_NAME + " 9102\n" +
        "#define " + MARKER_NAME + " 1", 1)
    bridge = '''
#if defined(GETDOC_GD31J24_READY_EVENT_BRIDGE)
\t\t// LAB COPY ONLY: the JS/CEF notification is not an authenticated
\t\t// import completion ACK and does not authorize save, WORM or printing.
\t\tif (pListener)
\t\t{
\t\t\tNSEditorApi::CAscCefMenuEvent* pReady =
\t\t\t\tm_pParent->CreateCefEvent(ASC_MENU_EVENT_TYPE_GETDOC_DOCUMENT_CONTENT_READY);
\t\t\tpListener->OnEvent(pReady);
\t\t}
#endif
'''
    marker = "\t\treturn true;\n\t}"
    assert HANDLER_ANCHOR.count(marker) == 1
    replaced_handler = HANDLER_ANCHOR.replace(marker, bridge + marker, 1)
    source = source.replace(HANDLER_ANCHOR, replaced_handler, 1)
    return header, source


def git(root: pathlib.Path, *args: str) -> str:
    try:
        return subprocess.check_output(
            ["git", "-C", str(root), *args],
            text=True, encoding="utf-8", stderr=subprocess.DEVNULL,
            timeout=15)
    except (subprocess.SubprocessError, OSError, UnicodeError) as exc:
        raise OverlayError("No local pinned Git checkout; never fetch SDK online") from exc


def check_checkout(root: pathlib.Path, *, require_clean: bool) -> None:
    if not root.is_absolute() or not root.is_dir() or root.is_symlink():
        raise OverlayError("A separate absolute, non-symlink checkout is required")
    marker = root / APPROVAL_SENTINEL
    if marker.is_symlink() or not marker.is_file() or marker.read_text(
            encoding="utf-8") != SENTINEL_TEXT:
        raise OverlayError("No explicit disposable offline SDK lab approval marker")
    if git(root, "rev-parse", "HEAD").strip() != UPSTREAM_PIN:
        raise OverlayError("Upstream SDK commit is not the reviewed pin")
    changes = git(root, "status", "--porcelain", "--untracked-files=no").strip("\n")
    if require_clean and changes:
        raise OverlayError("Never patch an already modified SDK checkout")
    if not require_clean:
        changed = {line[3:] for line in changes.splitlines() if line}
        if changed != {p.as_posix() for p in CHANGED_FILES}:
            raise OverlayError("Unexpected tracked changes in patched SDK lab copy")


def source_text(root: pathlib.Path, path: pathlib.Path) -> str:
    return git(root, "show", "HEAD:" + path.as_posix())


def _reject_links(root: pathlib.Path) -> None:
    for relative in CHANGED_FILES:
        candidate = root
        for part in relative.parts:
            candidate = candidate / part
            if candidate.is_symlink():
                raise OverlayError("SDK overlay path contains a symbolic link")


def verify_overlay(root: pathlib.Path) -> None:
    check_checkout(root, require_clean=False)
    _reject_links(root)
    expected = render_overlay(source_text(root, HEADER), source_text(root, SOURCE))
    for relative, expected_content in zip(CHANGED_FILES, expected):
        if (root / relative).read_text(encoding="utf-8") != expected_content:
            raise OverlayError("Patched SDK differs from the exact reviewed overlay")


def apply_overlay(root: pathlib.Path, *, acknowledged: bool) -> None:
    if not acknowledged:
        raise OverlayError("Apply requires explicit --ack-disposable-lab-copy")
    check_checkout(root, require_clean=True)
    _reject_links(root)
    before = tuple((root / p).read_text(encoding="utf-8") for p in CHANGED_FILES)
    originals = tuple(source_text(root, p) for p in CHANGED_FILES)
    if before != originals:
        raise OverlayError("Current files differ from pinned Git HEAD")
    patched = render_overlay(*before)
    try:
        for path, contents in zip(CHANGED_FILES, patched):
            (root / path).write_text(contents, encoding="utf-8", newline="")
        verify_overlay(root)
    except Exception:
        # Fail closed: restore the clean local copy on any partial failure.
        for path, contents in zip(CHANGED_FILES, before):
            (root / path).write_text(contents, encoding="utf-8", newline="")
        raise


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--verify", metavar="SDK_COPY", type=pathlib.Path)
    mode.add_argument("--apply", metavar="SDK_COPY", type=pathlib.Path)
    parser.add_argument("--ack-disposable-lab-copy", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.verify:
            verify_overlay(args.verify)
        else:
            apply_overlay(args.apply, acknowledged=args.ack_disposable_lab_copy)
    except (OverlayError, OSError, ValueError) as exc:
        print("GD31J24_REFUSED: " + str(exc), file=sys.stderr)
        return 2
    print("GD31J24_LAB_OVERLAY_VERIFIED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
