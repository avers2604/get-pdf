"""Offline GD-31J.24 pinned SDK ready-event overlay test.

Synthetic text tests only. No Qt/CEF, git, native build or SDK source changed.
"""
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import ready_bridge_overlay as overlay


class ReadyBridgeOverlayTests(unittest.TestCase):
    def test_requires_exact_unique_pinned_header_and_handler(self):
        header = overlay.HEADER_ANCHOR
        source = overlay.HANDLER_ANCHOR
        amended_header, amended_source = overlay.render_overlay(header, source)
        self.assertEqual(1, amended_header.count(overlay.EVENT_NAME))
        self.assertEqual(1, amended_source.count(overlay.EVENT_NAME))
        self.assertIn("pListener->OnEvent(pReady)", amended_source)
        self.assertIn("m_pParent->CreateCefEvent(", amended_source)
        self.assertIn("GETDOC_GD31J24_READY_EVENT_BRIDGE", amended_header)

        for bad_header in ("", header + header, amended_header):
            with self.assertRaises(overlay.OverlayError):
                overlay.render_overlay(bad_header, source)
        for bad_source in ("", source + source, amended_source):
            with self.assertRaises(overlay.OverlayError):
                overlay.render_overlay(header, bad_source)

    def test_no_patch_to_unapproved_source_or_dirty_checkout(self):
        with tempfile.TemporaryDirectory() as dirname:
            location = pathlib.Path(dirname)
            with self.assertRaises(overlay.OverlayError):
                overlay.check_checkout(location, require_clean=True)
            (location / overlay.APPROVAL_SENTINEL).write_text(
                overlay.SENTINEL_TEXT, encoding="utf-8")
            with patch.object(overlay, "git", side_effect=lambda root, *args: (
                overlay.UPSTREAM_PIN if args == ("rev-parse", "HEAD") else
                " M other_sensitive.cpp\n"
            )):
                with self.assertRaises(overlay.OverlayError):
                    overlay.check_checkout(location, require_clean=True)

    def test_explicit_source_pins_are_protected(self):
        self.assertEqual(40, len(overlay.UPSTREAM_PIN))
        self.assertEqual(2, len(overlay.CHANGED_FILES))
        self.assertIn("cefview.cpp", str(overlay.CHANGED_FILES[1]))
        self.assertIn("applicationmanager_events.h", str(overlay.CHANGED_FILES[0]))
        self.assertNotIn("open-local", overlay.EVENT_NAME)

    def test_overlay_rejects_untrusted_path_and_duplicate_event(self):
        with tempfile.TemporaryDirectory() as dirname:
            source_root = pathlib.Path(dirname)
            with self.assertRaises(overlay.OverlayError):
                overlay.verify_overlay(source_root)
            with self.assertRaises(overlay.OverlayError):
                overlay.apply_overlay(source_root, acknowledged=False)


if __name__ == "__main__":
    unittest.main()
