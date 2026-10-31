"""GD-31J.25 synthetic-only OOXML post-save checks. Tests are deferred until local acceptance."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from zipfile import ZipFile, ZIP_STORED

sys.path.insert(0, str(Path(__file__).resolve().parent))
import synthetic_roundtrip as lab

FIXTURES = {
    "gd31j6_writer.docx": (
        "word/document.xml", "word/document.xml",
        b'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>'),
    "gd31j6_calc.xlsx": (
        "xl/workbook.xml", "xl/sharedStrings.xml",
        b'<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>'),
    "gd31j6_impress.pptx": (
        "ppt/presentation.xml", "ppt/slides/slide1.xml",
        b'<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>'),
}
CLOSINGS = {
    ".docx": b'</w:t></w:r></w:p></w:body></w:document>',
    ".xlsx": b'</t></si></sst>',
    ".pptx": b'</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>',
}
MAIN = {
    ".docx": b'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body/></w:document>',
    ".xlsx": b'<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"/>',
    ".pptx": b'<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"/>',
}


def package(name, marker="", extra=None):
    suffix = Path(name).suffix
    main, text_part, prefix = FIXTURES[name]
    from io import BytesIO
    buf = BytesIO()
    with ZipFile(buf, "w", ZIP_STORED) as arc:
        content_type = {
            ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            ".xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            ".pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
        }[suffix]
        arc.writestr("[Content_Types].xml",
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Override PartName="/' + main + '" ContentType="' + content_type + '"/></Types>')
        arc.writestr("_rels/.rels",
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="' + main + '"/></Relationships>')
        if text_part != main:
            arc.writestr(main, MAIN[suffix])
        arc.writestr(text_part, prefix + marker.encode() + CLOSINGS[suffix])
        for entry, value in (extra or {}).items():
            arc.writestr(entry, value)
    return buf.getvalue()


def environment(tmp, name):
    root = Path(tmp)
    outputs = root / "gd31j25-output"
    outputs.mkdir()
    (outputs / "gd31j25-output-lab.marker").write_text(
        "GETDOC_GD31J25_DISPOSABLE_OUTPUT_ONLY\n", encoding="utf-8")
    original = package(name, "BEFORE")
    (root / name).write_bytes(original)
    manifest = {"schema": "gd31j6-office-fixtures/1",
                "purpose": "test-only-local-ooxml-no-corporate-documents",
                "files": {n: {"size": 1, "sha256": "0"*64} for n in FIXTURES}}
    manifest["files"][name] = {"size": len(original),
                                "sha256": hashlib.sha256(original).hexdigest()}
    (root / "gd31j6-fixture-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    destination = outputs / lab.OUTPUTS[name]
    destination.write_bytes(package(name, lab.CANARIES[name]))
    return root, destination, original


class SyntheticRoundTripTests(unittest.TestCase):
    def test_three_formats_verify_edited_output_without_touching_original(self):
        for name in FIXTURES:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                root, output, original = environment(tmp, name)
                result = lab.verify(root, name)
                self.assertTrue(result["syntheticPackageVerified"])
                self.assertFalse(result["nativeSaveProven"])
                self.assertFalse(result["approvedForProduction"])
                self.assertNotEqual(result["outputSha256"], result["sourceSha256"])
                self.assertEqual((root / name).read_bytes(), original)

    def test_unmodified_copy_does_not_pass(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, "gd31j6_writer.docx")
            output.write_bytes((root / "gd31j6_writer.docx").read_bytes())
            with self.assertRaises(lab.LabError):
                lab.verify(root, "gd31j6_writer.docx")

    def test_diff_without_canary_does_not_pass(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, "gd31j6_calc.xlsx")
            output.write_bytes(package("gd31j6_calc.xlsx", "DIFFERENT_NOT_CANARY"))
            with self.assertRaises(lab.LabError):
                lab.verify(root, "gd31j6_calc.xlsx")

    def test_manifest_hash_or_source_mutation_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _ = environment(tmp, "gd31j6_impress.pptx")
            (root / "gd31j6_impress.pptx").write_bytes(b"mutated")
            with self.assertRaises(lab.LabError):
                lab.verify(root, "gd31j6_impress.pptx")

    def test_incorrect_output_extension_or_unapproved_name_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, _, _ = environment(tmp, "gd31j6_writer.docx")
            for name in ("../gd31j6_writer.docx", "confidential.docx",
                         "gd31j6_writer.docx/../foo.docx"):
                with self.assertRaises(lab.LabError):
                    lab.verify(root, name)

    def test_path_link_and_missing_marker_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, "gd31j6_writer.docx")
            output.unlink()
            output.symlink_to(root / "gd31j6_writer.docx")
            with self.assertRaises(lab.LabError):
                lab.verify(root, "gd31j6_writer.docx")
            output.unlink()
            output.write_bytes(package("gd31j6_writer.docx", lab.CANARIES["gd31j6_writer.docx"]))
            (output.parent / "gd31j25-output-lab.marker").unlink()
            with self.assertRaises(lab.LabError):
                lab.verify(root, "gd31j6_writer.docx")

    def test_zip_traversal_duplicate_macro_and_external_relationships_rejected(self):
        name = "gd31j6_writer.docx"
        cases = [
            {"../escape.txt": "oops"},
            {"word/vbaProject.bin": b"unsafe"},
            {"word/_rels/document.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="x" Target="https://example.org" TargetMode="External"/></Relationships>'},
            {"word/document.xml": b"duplicate"},
        ]
        for extra in cases:
            with self.subTest(extra=list(extra)), tempfile.TemporaryDirectory() as tmp:
                root, output, _ = environment(tmp, name)
                output.write_bytes(package(name, lab.CANARIES[name], extra))
                with self.assertRaises(lab.LabError):
                    lab.verify(root, name)

    def test_corrupted_non_xml_part_crc_is_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, "gd31j6_writer.docx")
            data = package("gd31j6_writer.docx", lab.CANARIES["gd31j6_writer.docx"],
                           {"word/media/image1.png": b"CRCCHECKMARKER"})
            output.write_bytes(data.replace(b"CRCCHECKMARKER", b"CRCCHECKMARKEx"))
            with self.assertRaises(lab.LabError):
                lab.verify(root, "gd31j6_writer.docx")

    def test_embedded_canary_in_non_text_metadata_is_insufficient(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, name)
            output.write_bytes(package(name, "NOT_CHANGED", {"docProps/custom.xml": lab.CANARIES[name]}))
            with self.assertRaises(lab.LabError):
                lab.verify(root, name)

    def test_required_ooxml_main_relationship_and_content_type_must_match(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, name)
            wrong_relation = package(name, lab.CANARIES[name]).replace(
                b'Target="word/document.xml"', b'Target="xl/workbook.xml"')
            output.write_bytes(wrong_relation)
            with self.assertRaises(lab.LabError):
                lab.verify(root, name)
            wrong_type = package(name, lab.CANARIES[name]).replace(
                b'application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml',
                b'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml')
            output.write_bytes(wrong_type)
            with self.assertRaises(lab.LabError):
                lab.verify(root, name)

    def test_xml_doctype_and_bad_package_rejected(self):
        name = "gd31j6_writer.docx"
        with tempfile.TemporaryDirectory() as tmp:
            root, output, _ = environment(tmp, name)
            output.write_bytes(b"NOT A ZIP")
            with self.assertRaises(lab.LabError):
                lab.verify(root, name)
            output.write_bytes(package(name, lab.CANARIES[name], {
                "docProps/custom.xml": '<!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><foo/>'
            }))
            with self.assertRaises(lab.LabError):
                lab.verify(root, name)


if __name__ == "__main__":
    unittest.main()
