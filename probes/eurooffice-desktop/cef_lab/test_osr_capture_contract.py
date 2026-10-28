"""GD-31J.12 future offline-only source contract for real Wayland CEF OSR.

The native CEF test is not run in this phase. These assertions describe
exactly what must remain opt-in, Wayland-only, synthetic-only and untrusted.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent
SRC = ROOT / "cef_staged_open.cpp"
CMAKE = ROOT / "CMakeLists.txt"


class CefOsrSourceContractTests(unittest.TestCase):
    def test_real_upstream_callback_captured_but_ordinary_view_preserved(self):
        code = SRC.read_text("utf-8")
        self.assertIn("void OnPaint(const void* buffer, int width, int height) override", code)
        self.assertIn("QCefView::OnPaint(buffer, width, height);", code)
        self.assertIn("IsWayland()", code)
        self.assertIn("QMutexLocker", code)
        self.assertIn("QImage::Format_ARGB32_Premultiplied", code)
        self.assertIn("cefViewLoaded", code)

    def test_wayland_only_explicit_opt_in_and_existing_lab_unchanged(self):
        src = SRC.read_text("utf-8")
        cmake = CMAKE.read_text("utf-8")
        self.assertIn("--open-staged-wayland-osr-lab", src)
        self.assertIn("--open-staged-lab", src)
        self.assertIn('qgetenv("QT_QPA_PLATFORM")', src)
        self.assertIn('app.platformName()', src)
        self.assertIn("GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB", cmake)
        self.assertIn("OFF)", cmake)
        self.assertIn("GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB", cmake)
        self.assertIn("GETDOC_GD31J12_WAYLAND_OSR_CAPTURE", cmake)

    def test_receipt_never_claims_office_file_or_print_success(self):
        src = SRC.read_text("utf-8")
        for key in ('receipt["documentVerified"] = false;',
                    'receipt["saveVerified"] = false;',
                    'receipt["pdfVerified"] = false;',
                    'receipt["printApproved"] = false;',
                    'receipt["laboratoryOnly"] = true;',
                    'receipt["source"] = "cef-osr-wayland-synthetic-only";'):
            self.assertIn(key, src)
        self.assertIn('receipt["payloadBase64"]', src)
        self.assertIn('receipt["sha256"]', src)
        self.assertIn('GD31J12_OSR_CAPTURE_UNVERIFIED', src)

    def test_no_direct_printer_network_or_native_window_reparenting(self):
        src = SRC.read_text("utf-8")
        for forbidden in ("print-direct", "QPrinter", "SetParent(", "xcb_reparent_window",
                          "QDesktopServices::openUrl", "QProcess::startDetached"):
            self.assertNotIn(forbidden, src)


if __name__ == "__main__":
    unittest.main()
