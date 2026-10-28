#!/usr/bin/env python3
"""GD-31J.6: produce tiny OOXML fixtures in a NEW EMPTY offline lab directory.

Never accepts employee files, macros, network URLs or paths to existing
documents. No pip, NuGet, Microsoft Office, LibreOffice or Internet required.
These fixtures are for testing EuroOffice's native *OpenLocalFile* path ONLY.
"""
from __future__ import annotations

import argparse
from hashlib import sha256
import json
import os
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

FIXTURE_NAMES = (
    "gd31j6_writer.docx",
    "gd31j6_calc.xlsx",
    "gd31j6_impress.pptx",
)
MAIN_PARTS = {
    "gd31j6_writer.docx": "word/document.xml",
    "gd31j6_calc.xlsx": "xl/workbook.xml",
    "gd31j6_impress.pptx": "ppt/presentation.xml",
}
RELNS = "http://schemas.openxmlformats.org/package/2006/relationships"
OFFICEREL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
CTNS = "http://schemas.openxmlformats.org/package/2006/content-types"


def xml(text: str) -> bytes:
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
            text).encode("utf-8")


def types_xml(main: str, main_content_type: str, extras: dict[str, str]) -> bytes:
    overrides = {main: main_content_type, **extras}
    return xml(
        f'<Types xmlns="{CTNS}">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        + ''.join(f'<Override PartName="/{k}" ContentType="{v}"/>'
                  for k, v in overrides.items())
        + '</Types>'
    )


def root_relationships(main: str) -> bytes:
    return xml(
        f'<Relationships xmlns="{RELNS}">'
        f'<Relationship Id="rId1" Type="{OFFICEREL}/officeDocument" Target="{main}"/>'
        '</Relationships>'
    )


def parts_writer() -> dict[str, bytes]:
    main = "word/document.xml"
    return {
        "[Content_Types].xml": types_xml(
            main, "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", {}),
        "_rels/.rels": root_relationships(main),
        main: xml(
            '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
            '<w:body><w:p><w:r><w:t>ГЭТ — только испытательный документ GD-31J.6</w:t></w:r></w:p>'
            '<w:sectPr/></w:body></w:document>'
        ),
    }


def parts_calc() -> dict[str, bytes]:
    main = "xl/workbook.xml"
    sheet = "xl/worksheets/sheet1.xml"
    return {
        "[Content_Types].xml": types_xml(
            main, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            {sheet: "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"}),
        "_rels/.rels": root_relationships(main),
        main: xml(
            f'<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"'
            f' xmlns:r="{OFFICEREL}">'
            '<sheets><sheet name="Тест ГЭТ" sheetId="1" r:id="rId1"/></sheets></workbook>'
        ),
        "xl/_rels/workbook.xml.rels": xml(
            f'<Relationships xmlns="{RELNS}">'
            f'<Relationship Id="rId1" Type="{OFFICEREL}/worksheet" Target="worksheets/sheet1.xml"/>'
            '</Relationships>'
        ),
        sheet: xml(
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            '<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>ГЭТ — тест</t></is></c>'
            '<c r="B1"><f>2+2</f><v>4</v></c></row></sheetData></worksheet>'
        ),
    }


def parts_impress() -> dict[str, bytes]:
    main = "ppt/presentation.xml"
    slide = "ppt/slides/slide1.xml"
    return {
        "[Content_Types].xml": types_xml(
            main, "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
            {slide: "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"}),
        "_rels/.rels": root_relationships(main),
        main: xml(
            f'<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"'
            f' xmlns:r="{OFFICEREL}">'
            '<p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>'
            '<p:sldSz cx="9144000" cy="5143500"/>'
            '<p:notesSz cx="6858000" cy="9144000"/></p:presentation>'
        ),
        "ppt/_rels/presentation.xml.rels": xml(
            f'<Relationships xmlns="{RELNS}">'
            f'<Relationship Id="rId1" Type="{OFFICEREL}/slide" Target="slides/slide1.xml"/>'
            '</Relationships>'
        ),
        slide: xml(
            '<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" '
            'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">'
            '<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/>'
            '<p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>'
            '<p:sp><p:nvSpPr><p:cNvPr id="2" name="Тестовый заголовок"/>'
            '<p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/>'
            '<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>ГЭТ — тестовый слайд</a:t>'
            '</a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld>'
            '<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>'
        ),
    }


BUILDERS = {
    FIXTURE_NAMES[0]: parts_writer,
    FIXTURE_NAMES[1]: parts_calc,
    FIXTURE_NAMES[2]: parts_impress,
}


def prepare(root: Path) -> dict:
    if not root.is_absolute():
        raise ValueError("A fully qualified local staging root is required")
    # Do NOT follow an existing symlink in any ancestor.
    current = Path(root.anchor)
    for part in root.parts[1:]:
        current = current / part
        if current.is_symlink():
            raise ValueError("Links are forbidden in the lab staging path")
    if str(root).startswith(("\\\\", "//")):
        raise ValueError("Network staging is forbidden")
    root.mkdir(parents=True, exist_ok=True)
    if any(root.iterdir()):
        raise FileExistsError("Fixture staging must be empty, no existing files may be changed")

    manifest = {"schema": "gd31j6-office-fixtures/1",
                "purpose": "test-only-local-ooxml-no-corporate-documents",
                "files": {}}
    for name, builder in BUILDERS.items():
        file = root / name
        # ZIP mode 'x' refuses to replace an existing file, even after a race.
        with ZipFile(file, mode="x", compression=ZIP_DEFLATED,
                     compresslevel=6) as archive:
            for member, data in builder().items():
                archive.writestr(member, data)
        raw = file.read_bytes()
        manifest["files"][name] = {"sha256": sha256(raw).hexdigest(), "size": len(raw)}

    with (root / "gd31j6-fixture-manifest.json").open("x", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, sort_keys=True, indent=2)
        handle.write("\n")
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate ONLY local office lab fixtures")
    parser.add_argument("--out", type=Path, required=True,
                        help="NEW EMPTY absolute local directory; refuses overwriting files")
    args = parser.parse_args(argv)
    try:
        manifest = prepare(args.out)
    except (OSError, ValueError) as e:
        parser.exit(2, "BLOCKED: " + str(e) + "\n")
    print("Prepared", len(manifest["files"]), "synthetic OOXML fixtures; no private files.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
