"""GD-31J.18 offline-only static interface contracts (NOT executed yet).

Pin the actual Avalonia 12.1.4 TextInputMethodClient API and ensure
no fake caret derived from mouse/OnPaint activates the OS IME.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
DESKTOP=ROOT/"src/GetDocument.Desktop"

class NativeImeClientSourceContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.adapter=(DESKTOP/"Office/AvaloniaNativeImeLabClient.cs").read_text("utf-8")
        cls.pane=(DESKTOP/"Office/NativeOfficeDiagnosticPane.cs").read_text("utf-8")
        cls.geometry=(OFFICE/"EuroOfficeImeCaretGeometry.cs").read_text("utf-8")
        cls.project=(DESKTOP/"GetDocument.Desktop.csproj").read_text("utf-8")

    def test_avalonia_12_1_4_client_api_and_default_caret_gate(self):
        for needle in (
            "Avalonia.Desktop", 'Version="12.1.4"'
        ): self.assertIn(needle,self.project)
        for needle in (
            ": TextInputMethodClient, IDisposable",
            "override Visual TextViewVisual",
            "override bool SupportsPreedit => true",
            "override bool SupportsSurroundingText => false",
            "override string SurroundingText => string.Empty",
            "override Rect CursorRectangle",
            "override TextSelection Selection",
            "override void SetPreeditText(string? preeditText, int? cursorPos)",
            "_hasCaret && _sessionActive()", "RaiseCursorRectangleChanged()",
            "RequestReset()", "EuroOfficeImeCaretGeometry.MapToPreview("
        ): self.assertIn(needle,self.adapter)

    def test_cannot_register_os_ime_without_editor_caret(self):
        self.assertIn("OnLabImeClientRequested(",self.pane)
        self.assertIn("TextInputMethodClientRequested += OnLabImeClientRequested",self.pane)
        self.assertIn("CanRegisterWithOsIme: true",self.pane)
        self.assertIn("e.Client = client;",self.pane)
        self.assertIn("_nativeImeClient?.Revoke();",self.pane)
        self.assertIn("_nativeImeClient?.Dispose();",self.pane)
        self.assertNotIn("SupplyCaretFromFutureAuthenticatedNativeAdapter(",self.pane)
        self.assertNotIn("GetPosition(_preview)",self.adapter)
        self.assertNotIn("PointerMoved",self.adapter)

    def test_caret_geometry_requires_session_tab_frame_match(self):
        for needle in ("SessionId != expectedSession", "TabId != expectedTab",
                       "caret.FrameNumber != expectedFrameNumber",
                       "double.IsFinite(number)", "Math.Min(previewLogicalWidth",
                       "PhysicalX + caret.PhysicalWidth",
                       "PhysicalY + caret.PhysicalHeight"):
            self.assertIn(needle,self.geometry)

    def test_windows_and_x11_use_fallback_without_hwnd_xid_reparent(self):
        self.assertIn('qpaPlatform, "wayland"',self.geometry)
        self.assertIn('ProductionFallback = "LibreOffice/UNO"',self.geometry)
        for forbidden in ("SetParent(", "XReparentWindow(", "xcb_reparent_window("):
            self.assertNotIn(forbidden,self.adapter)
            self.assertNotIn(forbidden,self.geometry)

    def test_no_unapproved_clipboard_or_surrounding_text_export(self):
        self.assertNotIn("Clipboard",self.adapter.split("class AvaloniaNativeImeLabClient")[1].split("public override")[0])
        self.assertNotIn("GetTextAsync",self.adapter)
        self.assertNotIn("GetDataObject",self.adapter)
        self.assertIn("new(0, 0)",self.adapter)
        self.assertIn('throw new NotSupportedException(',self.adapter)
        self.assertIn("OnLabImePreeditChanged(",self.pane)


if __name__=="__main__":
    unittest.main()
