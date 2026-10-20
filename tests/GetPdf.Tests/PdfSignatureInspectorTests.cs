using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfSignatureInspectorTests
{
    [TestMethod]
    public void UnsignedPdfNeverReportsVerifiedSignatures()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-signatures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "unsigned.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.Save(path); }
            var result = PdfSignatureInspector.VerifyOffline(path);
            Assert.AreEqual(0, result.Count);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void UnrecognizedSignatureMarkerMustNotBeReportedAsUnsigned()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-unrecognized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "ambiguous.pdf");
            using (var pdf = new PdfDocument())
            {
                var page = pdf.AddPage();
                var forged = new PdfSharp.Pdf.PdfDictionary(pdf);
                var range = new PdfSharp.Pdf.PdfArray(pdf);
                foreach (var n in new[] { 0, 10, 100, 20 })
                    range.Elements.Add(new PdfSharp.Pdf.PdfInteger(n));
                forged.Elements.SetObject("/ByteRange", range);
                pdf.Internals.Catalog.Elements.SetObject("/SuspiciousSignature", forged);
                pdf.Save(path);
            }
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PdfSignatureInspector.VerifyOffline(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void SignatureStatusNeedsCryptographyAndLocalTrust()
    {
        Assert.AreEqual(PdfSignatureTrust.Unverified,
            PdfSignatureInspector.Classify(true, false, true));
        Assert.AreEqual(PdfSignatureTrust.Failed,
            PdfSignatureInspector.Classify(false, true, true));
        Assert.AreEqual(PdfSignatureTrust.RevisionOnly,
            PdfSignatureInspector.Classify(true, true, false));
        Assert.AreEqual(PdfSignatureTrust.LocallyTrusted,
            PdfSignatureInspector.Classify(true, true, true));
    }
}
