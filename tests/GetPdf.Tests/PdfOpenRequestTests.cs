using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf;

[TestClass]
public sealed class PdfOpenRequestTests
{
    [TestMethod]
    public void ExistingPdfOpensOnlyWithOneArgument()
    {
        var path = Path.Combine(Path.GetTempPath(), "getpdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]);
            Assert.IsTrue(PdfOpenRequest.TryParse([path], out var parsed));
            Assert.AreEqual(path, parsed);
            Assert.IsFalse(PdfOpenRequest.TryParse([path, path], out _));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void RefusesNonPdfMissingAndUrl()
    {
        Assert.IsFalse(PdfOpenRequest.TryParse([], out _));
        Assert.IsFalse(PdfOpenRequest.TryParse(["https://example.org/document.pdf"], out _));
        Assert.IsFalse(PdfOpenRequest.TryParse(["missing.pdf"], out _));
    }
}
