"""GD-31J.36: CEF stdout v1/v2 bounded transport-to-parser alignment.

Static verification only. Does NOT launch Qt, compile, parse native output,
test filesystem durability, or grant Office/WORM publication privileges.
"""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[3]
OFFICE=ROOT/"src/GetDocument.Office/EuroOfficeDesktop"
TESTS=ROOT/"tests/GetDocument.Office.Tests"

class NativeV2TransportBoundSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.transport=(OFFICE/"EuroOfficeNativeBindingProbeLab.cs").read_text(encoding="utf-8")
        cls.saveas=(OFFICE/"EuroOfficeNativeSaveAsLab.cs").read_text(encoding="utf-8")
        cls.v2=(OFFICE/"EuroOfficeNativeSaveAsEventReceipt.cs").read_text(encoding="utf-8")
        cls.v1=(OFFICE/"EuroOfficeNativeSaveAsReceipt.cs").read_text(encoding="utf-8")
        cls.transport_tests=(TESTS/"EuroOfficeNativeBindingProbeLabTests.cs").read_text(encoding="utf-8")
        cls.v2_tests=(TESTS/"EuroOfficeNativeSaveAsEventReceiptTests.cs").read_text(encoding="utf-8")

    def test_v2_parser_and_transport_agree_on_strict_4096_limit(self):
        self.assertIn("raw.Length is < 2 or > 4096",self.v2)
        self.assertIn("maxReceiptChars: 4096",self.saveas)
        self.assertIn("maxReceiptChars = 2048",self.transport)
        self.assertIn("int maxChars = 2048",self.transport)
        self.assertIn("maxChars is not (2048 or 4096)",self.transport)
        self.assertIn("RequireBoundedReceiptLimit(maxChars)",self.transport)
        self.assertIn("RequireBoundedReceiptLimit(maxReceiptChars)",self.transport)
        self.assertIn("process.StandardOutput, deadline.Token, maxReceiptChars",self.transport)
        self.assertIn("RunRawLabAsync(info, cancellationToken)",self._binding_runner())

    def _binding_runner(self):
        return self.transport

    def test_legacy_receipt_bound_and_strict_stdout_controls_remain(self):
        self.assertIn("raw.Length is < 2 or > 2048",self.v1)
        for marker in (
            "receipt.Length >= maxChars",
            "if (ended || receipt.Length == 0)",
            "if (!ended)",
            "ch is '\\r' or '\\0' || char.IsControl(ch)",
            "if (process.ExitCode != 0)",
            "process.Kill(entireProcessTree: true)"
        ):
            self.assertIn(marker,self.transport)

    def test_regressions_cover_boundary_both_directions_and_v2_parser(self):
        for marker in (
            "ExplicitSaveAsV2TransportAcceptsBoundedReceiptLargerThanLegacy",
            "SaveAsV2StillRejectsOverLimitOutputExtraLinesAndMissingNewline",
            "LegacyBindingReceiptKeepsOriginal2048CharacterLimit",
            "ArbitraryTransportLimitCannotBeUsedToDisableSizeGate"
        ):
            self.assertIn(marker,self.transport_tests)
        self.assertIn("BoundedV2TransportAndStrictParserAcceptSameNestedEnvelope",
                      self.v2_tests)
        self.assertIn("Assert.IsFalse(result.AuthorizedForPublication)",self.v2_tests)

if __name__=="__main__":
    unittest.main()
