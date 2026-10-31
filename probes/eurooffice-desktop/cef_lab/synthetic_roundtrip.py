#!/usr/bin/env python3
"""GD-31J.25: independent, READ-ONLY OOXML artifact checker for an isolated lab.

This does not call or authenticate EuroOffice. A passing package is a synthetic
canary/ZIP candidate, NOT native save proof, production readiness or WORM ACL.
Only run with manually approved disposable offline fixtures after an external
native save attempt has finished; no write functions exist in this module.
"""
import argparse
from io import BytesIO
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import zlib
import xml.etree.ElementTree as ET
import zipfile

FIXTURES = (
    "gd31j6_writer.docx",
    "gd31j6_calc.xlsx",
    "gd31j6_impress.pptx",
)
OUTPUTS = {
    "gd31j6_writer.docx": "gd31j25_writer_saved.docx",
    "gd31j6_calc.xlsx": "gd31j25_calc_saved.xlsx",
    "gd31j6_impress.pptx": "gd31j25_impress_saved.pptx",
}
CANARIES = {
    "gd31j6_writer.docx": "GD31J25_SYNTHETIC_WRITER_EDIT_6A9B",
    "gd31j6_calc.xlsx": "GD31J25_SYNTHETIC_CALC_EDIT_7B8C",
    "gd31j6_impress.pptx": "GD31J25_SYNTHETIC_IMPRESS_EDIT_8C9D",
}
MAX_FILE = 32 * 1024 * 1024
MAX_UNCOMPRESSED = 64 * 1024 * 1024
MAX_PART = 16 * 1024 * 1024
MAX_ENTRIES = 256
MAX_RATIO = 200
CONTENT_TYPES = "[Content_Types].xml"
PACKAGE_RELS = "_rels/.rels"
OUTPUT_MARKER = "gd31j25-output-lab.marker"
OUTPUT_MARKER_TEXT = b"GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n"
NS = {
    "content": "http://schemas.openxmlformats.org/package/2006/content-types",
    "rels": "http://schemas.openxmlformats.org/package/2006/relationships",
    "w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
    "s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
}
REQUIRED_MAIN = {
    ".docx": ("word/document.xml", "{"+NS["w"]+"}document"),
    ".xlsx": ("xl/workbook.xml", "{"+NS["s"]+"}workbook"),
    ".pptx": ("ppt/presentation.xml", "{"+NS["p"]+"}presentation"),
}


class LabError(ValueError):
    """Fail closed; error does not reveal document content."""


def _must_be_unlinked_local(path: Path, *, directory: bool = False) -> None:
    if (not path.is_absolute() or str(path).startswith("//") or
            "://" in str(path) or "\x00" in str(path)):
        raise LabError("Only absolute local lab paths are allowed")
    current = path
    while True:
        try:
            mode = current.lstat().st_mode
        except OSError as exc:
            raise LabError("Missing lab path component") from exc
        if stat.S_ISLNK(mode):
            raise LabError("Linked lab path component")
        if current == path:
            if directory and not stat.S_ISDIR(mode):
                raise LabError("Lab directory is not a directory")
            if not directory and not stat.S_ISREG(mode):
                raise LabError("Lab file is not regular")
        parent = current.parent
        if parent == current:
            break
        current = parent


def _read_limited(path: Path, limit: int) -> bytes:
    _must_be_unlinked_local(path)
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        fd = os.open(path, flags)
        with os.fdopen(fd, "rb") as stream:
            before = os.fstat(stream.fileno())
            if not stat.S_ISREG(before.st_mode) or before.st_size <= 0 or before.st_size > limit:
                raise LabError("Invalid size or kind of lab file")
            data = stream.read(limit + 1)
            after = os.fstat(stream.fileno())
            if len(data) != before.st_size or len(data) > limit or (
                before.st_size != after.st_size or
                before.st_mtime_ns != after.st_mtime_ns or
                before.st_ctime_ns != after.st_ctime_ns
            ):
                raise LabError("Lab file changed during inspection")
            return data
    except OSError as exc:
        raise LabError("Lab file could not be opened") from exc


def _xml(raw: bytes) -> ET.Element:
    # stdlib ElementTree does not expand external entities, but explicitly
    # reject DTD declarations even if the parser would tolerate them.
    lower = raw.lower()
    if b"<!doctype" in lower or b"<!entity" in lower or b"<?xml-stylesheet" in lower:
        raise LabError("Active XML markup in a synthetic package")
    try:
        return ET.fromstring(raw)
    except ET.ParseError as exc:
        raise LabError("Malformed OOXML XML part") from exc


def _part_name(name: str) -> None:
    pieces = name.split("/")
    if (not name or name.startswith("/") or "\\" in name or "\x00" in name or
            any(piece in ("", ".", "..") for piece in pieces) or
            ":" in name or len(name) > 240):
        raise LabError("Unsafe OOXML ZIP part name")
    lowered = "/" + name.lower()
    if (name.lower().endswith((".bin", ".exe", ".js", ".vbs")) or
        "/activex/" in lowered or "/embeddings/" in lowered or
        "/externallinks/" in lowered or "/vba" in lowered):
        raise LabError("Active or external content is forbidden in synthetic OOXML")


def _read_ooxml(data: bytes, suffix: str, canary: str) -> None:
    main, expected_root = REQUIRED_MAIN[suffix]
    names = set()
    text_matches = 0
    total = 0
    expected_text = {"w": "{"+NS["w"]+"}t", "s": "{"+NS["s"]+"}t",
                     "a": "{"+NS["a"]+"}t"}
    try:
        with zipfile.ZipFile(BytesIO(data), "r") as archive:
            items = archive.infolist()
            if not (1 <= len(items) <= MAX_ENTRIES):
                raise LabError("OOXML ZIP entry count is outside the limit")
            for item in items:
                name = item.filename
                _part_name(name)
                if name in names or item.is_dir() or item.flag_bits & 1:
                    raise LabError("Duplicate, directory or encrypted OOXML part")
                names.add(name)
                kind = (item.external_attr >> 16) & 0o170000
                if kind not in (0, stat.S_IFREG):
                    raise LabError("Non-file OOXML ZIP entry")
                if item.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    raise LabError("Unexpected OOXML ZIP compression")
                if item.file_size > MAX_PART or item.file_size < 0:
                    raise LabError("Oversized OOXML part")
                if item.file_size and (item.compress_size <= 0 or
                    item.file_size > item.compress_size * MAX_RATIO):
                    raise LabError("OOXML decompression ratio exceeds lab budget")
                total += item.file_size
                if total > MAX_UNCOMPRESSED:
                    raise LabError("OOXML uncompressed budget exceeded")
            if not {CONTENT_TYPES, PACKAGE_RELS, main}.issubset(names):
                raise LabError("OOXML required package or main parts are missing")

            for item in items:
                # Reading ALL entries checks CRC, including binary media parts
                # that otherwise could be silently corrupt or truncated.
                raw = archive.read(item)
                name = item.filename
                if not name.endswith((".xml", ".rels")) and name != CONTENT_TYPES:
                    continue
                root = _xml(raw)
                if name == CONTENT_TYPES and root.tag != "{"+NS["content"]+"}Types":
                    raise LabError("Invalid OOXML content-type root")
                if name.endswith(".rels") or name == PACKAGE_RELS:
                    if root.tag != "{"+NS["rels"]+"}Relationships":
                        raise LabError("Invalid OOXML relationship root")
                    for relationship in root:
                        target = relationship.get("Target", "")
                        mode = relationship.get("TargetMode", "Internal")
                        if (mode != "Internal" or
                            target.startswith(("/", "\\")) or
                            re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target)):
                            raise LabError("External OOXML relationship in synthetic fixture")
                if name == main and root.tag != expected_root:
                    raise LabError("OOXML main document root does not match the extension")
                eligible = (
                    suffix == ".docx" and name == "word/document.xml" or
                    suffix == ".xlsx" and (
                        name == "xl/sharedStrings.xml" or
                        bool(re.fullmatch(r"xl/worksheets/sheet[1-9]\d*\.xml", name))) or
                    suffix == ".pptx" and
                    bool(re.fullmatch(r"ppt/slides/slide[1-9]\d*\.xml", name))
                )
                if eligible:
                    target_tag = (expected_text["w"] if suffix == ".docx" else
                                  expected_text["s"] if suffix == ".xlsx" else
                                  expected_text["a"])
                    text_matches += sum(1 for node in root.iter(target_tag)
                                        if node.text == canary)
            if text_matches != 1:
                raise LabError("Expected exactly one visible synthetic edit canary")
    except (OSError, EOFError, zlib.error, zipfile.BadZipFile, RuntimeError, ValueError) as exc:
        if isinstance(exc, LabError):
            raise
        raise LabError("Corrupt or unsupported OOXML ZIP") from exc


def verify(staging: Path | str, fixture_name: str) -> dict:
    """Read approved original and named output, verify bytes; never create/replace them.

    A valid result is NOT proof that EuroOffice produced the file. Record any
    native callback and actual UI edit as separate operator evidence.
    """
    if fixture_name not in FIXTURES:
        raise LabError("Unapproved synthetic OOXML fixture")
    root = Path(staging)
    _must_be_unlinked_local(root, directory=True)
    output_root = root / "gd31j25-output"
    _must_be_unlinked_local(output_root, directory=True)
    if _read_limited(output_root / OUTPUT_MARKER, 1024) != OUTPUT_MARKER_TEXT:
        raise LabError("Disposable output directory not explicitly approved")
    manifest_bytes = _read_limited(root / "gd31j6-fixture-manifest.json", 16 * 1024)
    try:
        def exact_object(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    raise LabError("Duplicated JSON manifest key")
                result[key] = value
            return result

        manifest = json.loads(
            manifest_bytes, object_pairs_hook=exact_object,
            parse_constant=lambda _: (_ for _ in ()).throw(LabError("Invalid JSON constant")))
        if (not isinstance(manifest, dict) or
            manifest.get("schema") != "gd31j6-office-fixtures/1" or
            manifest.get("purpose") != "test-only-local-ooxml-no-corporate-documents" or
            not isinstance(manifest.get("files"), dict) or
            set(manifest["files"]) != set(FIXTURES)):
            raise LabError("Unapproved synthetic source manifest")
        entry = manifest["files"][fixture_name]
        if not isinstance(entry, dict) or set(entry) != {"size", "sha256"}:
            raise LabError("Invalid fixture manifest entry")
        size = entry["size"]
        digest = entry["sha256"]
        if (type(size) is not int or not 0 < size <= MAX_FILE or
            not isinstance(digest, str) or
            not re.fullmatch("[0-9a-f]{64}", digest)):
            raise LabError("Invalid synthetic original SHA/size attestation")
    except (UnicodeError, json.JSONDecodeError, TypeError, KeyError) as exc:
        raise LabError("Malformed synthetic manifest") from exc
    source_path = root / fixture_name
    original = _read_limited(source_path, MAX_FILE)
    original_digest = hashlib.sha256(original).hexdigest()
    if len(original) != size or original_digest != digest:
        raise LabError("Synthetic source changed since fixture manifest")
    output = _read_limited(output_root / OUTPUTS[fixture_name], MAX_FILE)
    output_digest = hashlib.sha256(output).hexdigest()
    if output_digest == original_digest:
        raise LabError("Output is byte-identical to approved source")
    _read_ooxml(output, Path(fixture_name).suffix, CANARIES[fixture_name])
    if hashlib.sha256(_read_limited(source_path, MAX_FILE)).hexdigest() != original_digest:
        raise LabError("Synthetic original changed during round-trip validation")
    if hashlib.sha256(_read_limited(output_root / OUTPUTS[fixture_name], MAX_FILE)).hexdigest() != output_digest:
        raise LabError("Synthetic output changed during round-trip validation")
    return {
        "schema": "getdoc-synthetic-ooxml-roundtrip-candidate-lab/1",
        "fixtureName": fixture_name,
        "sourceSha256": original_digest,
        "outputSha256": output_digest,
        "syntheticPackageVerified": True,
        "nativeSaveProven": False,
        "documentVerified": False,
        "approvedForProduction": False,
        "laboratoryOnly": True,
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="READ-ONLY synthetic OOXML lab verifier")
    parser.add_argument("--staging", required=True, type=Path)
    parser.add_argument("--fixture", required=True, choices=FIXTURES)
    args = parser.parse_args(argv)
    try:
        result = verify(args.staging, args.fixture)
    except LabError as exc:
        print("GD31J25_REJECTED: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(result, separators=(",", ":"), sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
