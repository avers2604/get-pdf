"""Offline GD-31J.3 safety checks for the OPTIONAL CEF lifecycle lab.

No CEF process is started by these tests. Only exact pinned local sources and
explicit non-document, network-isolated lab settings may be accepted.
"""
import pathlib
import tempfile
import unittest
from unittest.mock import patch
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import cef_lab_gate


class CefLabGateTests(unittest.TestCase):
    def test_requires_independent_isolation_approval_and_local_runtime(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = pathlib.Path(dirname)
            self.assertRaises(
                cef_lab_gate.LabBlocked, cef_lab_gate.check_lab,
                root, isolation_confirmed=False, allow_documents=False)
            self.assertRaises(
                cef_lab_gate.LabBlocked, cef_lab_gate.check_lab,
                root, isolation_confirmed=True, allow_documents=True)

    def test_disallows_network_paths_and_symlink_directories(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = pathlib.Path(dirname)
            (root / "bundle").mkdir()
            (root / "link").symlink_to(root / "bundle", target_is_directory=True)
            with self.assertRaises(cef_lab_gate.LabBlocked):
                cef_lab_gate.check_lab(root / "link", isolation_confirmed=True,
                                       allow_documents=False)
            with self.assertRaises(cef_lab_gate.LabBlocked):
                cef_lab_gate.check_lab(pathlib.Path("relative/path"),
                                       isolation_confirmed=True, allow_documents=False)

    def test_missing_helper_and_web_assets_are_explicitly_blocked(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = pathlib.Path(dirname)
            with self.assertRaisesRegex(cef_lab_gate.LabBlocked, "helper"):
                cef_lab_gate.check_assets(
                    root, cef_lab_gate.AssetSet(
                        helper="editors_helper", libraries=("libcef.so",),
                        web_editor="editors/web-apps/apps/api/documents/index.html"))

    def test_correct_assets_still_do_not_enable_editing(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = pathlib.Path(dirname)
            files = (
                "editors_helper",
                "libcef.so",
                "editors/web-apps/apps/api/documents/index.html",
            )
            for name in files:
                entry = root / name
                entry.parent.mkdir(parents=True, exist_ok=True)
                entry.write_bytes(b"fixture")
            assessment = cef_lab_gate.check_assets(
                root, cef_lab_gate.AssetSet(
                    helper="editors_helper", libraries=("libcef.so",),
                    web_editor="editors/web-apps/apps/api/documents/index.html"))
            self.assertTrue(assessment.lifecycle_assets_present)
            self.assertFalse(assessment.document_editing_approved)
            self.assertFalse(assessment.production_ready)

    def test_upstream_revision_check_requires_exact_all_three_pins(self):
        expected = cef_lab_gate.REQUIRED_COMMITS
        self.assertEqual(3, len(expected))
        with patch.object(cef_lab_gate, "git_head", side_effect=lambda p: expected[p.name]), \
             patch.object(cef_lab_gate, "git_clean", return_value=True):
            with tempfile.TemporaryDirectory() as dirname:
                root = pathlib.Path(dirname) / "DesktopEditors"
                root.mkdir()
                (root / "desktop-sdk").mkdir()
                (root / "core").mkdir()
                cef_lab_gate.check_revision_pins(root)
        with patch.object(cef_lab_gate, "git_head", return_value="0" * 40), \
             patch.object(cef_lab_gate, "git_clean", return_value=True):
            with tempfile.TemporaryDirectory() as dirname:
                root = pathlib.Path(dirname) / "DesktopEditors"
                root.mkdir()
                (root / "desktop-sdk").mkdir()
                (root / "core").mkdir()
                with self.assertRaises(cef_lab_gate.LabBlocked):
                    cef_lab_gate.check_revision_pins(root)

    def test_asset_parent_symlink_is_rejected(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = pathlib.Path(dirname)
            trusted = root / "trusted"
            trusted.mkdir()
            (trusted / "editor").write_bytes(b"fake")
            (root / "linked").symlink_to(trusted, target_is_directory=True)
            with self.assertRaises(cef_lab_gate.LabBlocked):
                cef_lab_gate.check_assets(
                    root, cef_lab_gate.AssetSet(
                        helper="linked/editor", libraries=(), web_editor="linked/editor"))

    def test_git_dirty_source_checkout_is_rejected(self):
        with tempfile.TemporaryDirectory() as dirname:
            tree = pathlib.Path(dirname) / "DesktopEditors"
            tree.mkdir()
            (tree / "desktop-sdk").mkdir()
            (tree / "core").mkdir()
            with patch.object(cef_lab_gate, "git_head",
                              side_effect=lambda path: cef_lab_gate.REQUIRED_COMMITS[path.name]):
                with patch.object(cef_lab_gate, "git_clean", return_value=False):
                    with self.assertRaises(cef_lab_gate.LabBlocked):
                        cef_lab_gate.check_revision_pins(tree)

    def test_lab_assessment_fails_closed_for_document_editing_even_if_approved(self):
        with self.assertRaises(cef_lab_gate.LabBlocked):
            cef_lab_gate.check_lab(
                pathlib.Path("/tmp"),
                isolation_confirmed=True, allow_documents=True)


if __name__ == "__main__":
    unittest.main()
