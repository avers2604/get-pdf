"""GD-31J.6 local fixture generator regression tests; designed for offline run."""
import json
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile
import sys
import hashlib
sys.path.insert(0, str(Path(__file__).resolve().parent))
import prepare_office_fixtures as fixtures


class OfficeFixtureTests(unittest.TestCase):
    def test_generates_three_distinct_local_ooxml_documents_and_sha_manifest(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            manifest = fixtures.prepare(root)
            self.assertEqual("gd31j6-office-fixtures/1", manifest["schema"])
            self.assertEqual(sorted(fixtures.FIXTURE_NAMES), sorted(manifest["files"]))
            for name in fixtures.FIXTURE_NAMES:
                path = root / name
                self.assertTrue(path.is_file(), name)
                self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),
                                 manifest["files"][name]["sha256"])
                self.assertEqual(path.stat().st_size, manifest["files"][name]["size"])
                with ZipFile(path) as z:
                    self.assertIn("[Content_Types].xml", z.namelist())
                    self.assertIn("_rels/.rels", z.namelist())
                    self.assertIn(fixtures.MAIN_PARTS[name], z.namelist())
                    self.assertIsNone(z.testzip())
            self.assertEqual(manifest,
                             json.loads((root / "gd31j6-fixture-manifest.json").read_text("utf-8")))

    def test_refuses_existing_directory_content_to_avoid_employee_files(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            (root / "gd31j6_writer.docx").write_text("do-not-replace", encoding="utf-8")
            with self.assertRaises(FileExistsError):
                fixtures.prepare(root)
            self.assertEqual("do-not-replace",
                             (root / "gd31j6_writer.docx").read_text(encoding="utf-8"))

    def test_refuses_symlink_staging_root(self):
        with tempfile.TemporaryDirectory() as d:
            real = Path(d) / "real"
            real.mkdir()
            symlink = Path(d) / "link"
            symlink.symlink_to(real, target_is_directory=True)
            with self.assertRaises(ValueError):
                fixtures.prepare(symlink)

    def test_second_invocation_does_not_reuse_or_overwrite_files(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            fixtures.prepare(root)
            with self.assertRaises(FileExistsError):
                fixtures.prepare(root)


if __name__ == "__main__":
    unittest.main()
