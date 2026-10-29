"""GD-31J.21: test the native bridge's digest helper WITHOUT importing UNO.

The Python/UNO GUI executable is tested separately in the deferred offline lab.
These tests only compile a standalone, side-effect-free hash helper from the
real bridge source and inspect the save command's immutable protocol contract.
"""
import ast
import hashlib
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
BRIDGE = ROOT / "src/GetDocument.Office/LibreOffice/lo_editor.py"
EDITOR = ROOT / "src/GetDocument.Office/LibreOffice/LibreOfficeEditor.cs"


class NativeSaveReceiptContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = BRIDGE.read_text(encoding="utf-8")
        cls.editor = EDITOR.read_text(encoding="utf-8")

    def test_hash_helper_detects_same_length_same_timestamp_changes(self):
        tree = ast.parse(self.source)
        helpers = [node for node in tree.body
                   if isinstance(node, ast.FunctionDef) and node.name == "getdoc_file_sha256"]
        self.assertEqual(1, len(helpers), "native hash helper must be top-level and testable")
        isolated = ast.Module(body=helpers, type_ignores=[])
        ast.fix_missing_locations(isolated)
        scope = {"hashlib": hashlib, "open": open}
        exec(compile(isolated, str(BRIDGE), "exec"), scope)
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder) / "memo.docx"
            p.write_bytes(b"ORIGINAL")
            recorded = p.stat().st_mtime_ns
            old = scope["getdoc_file_sha256"](str(p))
            p.write_bytes(b"REPLACED")
            import os
            os.utime(p, ns=(recorded, recorded))
            new = scope["getdoc_file_sha256"](str(p))
            self.assertEqual(hashlib.sha256(b"REPLACED").hexdigest().upper(), new)
            self.assertNotEqual(old, new)

    def test_native_save_checks_expected_digest_before_uno_store(self):
        tree = ast.parse(self.source)
        bridge_class = next(n for n in tree.body if isinstance(n, ast.ClassDef)
                            and n.name == "Bridge")
        method = next(n for n in bridge_class.body if isinstance(n, ast.FunctionDef)
                      and n.name == "op_save")
        lines = ast.get_source_segment(self.source, method)
        self.assertIsNotNone(lines)
        self.assertIn('request.get("expectedSha256")', lines)
        self.assertIn("getdoc_file_sha256(entry.source_path)", lines)
        self.assertIn("GETDOC_SAVE_CONFLICT", lines)
        self.assertLess(lines.index("GETDOC_SAVE_CONFLICT"), lines.index("entry.doc.store()"))
        self.assertIn('"sha256": getdoc_file_sha256(entry.source_path)', lines)

    def test_managed_bridge_rejects_missing_or_mismatched_receipt(self):
        self.assertIn('expectedSha256 = expected', self.editor)
        self.assertIn('TryGetProperty("sha256"', self.editor)
        self.assertIn("OfficeFileConflictException()", self.editor)


if __name__ == "__main__":
    unittest.main()
