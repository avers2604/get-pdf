using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace GetPdf.Tests;
[TestClass]
public class PdfOnlyContractTests
{
    [TestMethod]
    public void OpensExistingPdfAndRejectsNonPdf()
    {
        string folder = Path.Combine(Path.GetTempPath(), "get-pdf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string pdf = Path.Combine(folder, "document.pdf");
            string scan = Path.Combine(folder, "photo.png");
            File.WriteAllText(pdf, "%PDF");
            File.WriteAllText(scan, "not-a-pdf");
            Assert.IsTrue(PdfOpenRequest.TryParse([pdf], out var accepted));
            Assert.AreEqual(pdf, accepted);
            Assert.IsFalse(PdfOpenRequest.TryParse([scan], out _));
            Assert.IsFalse(PdfOpenRequest.TryParse([pdf, pdf], out _));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    [TestMethod]
    public void ScannerSettingsAreNotAcceptedByPdfPolicy()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WorkstationPolicy.Parse("{\"Version\":1,\"RequireIsolatedScannerBackend\":true}"));
        Assert.IsFalse(new WorkstationPolicy { AllowPrinting = false }.AllowPrinting);
    }
}
