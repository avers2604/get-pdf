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
    public void UnrecognizedByteRangesNeverMeanUnsignedPdf()
    {
        PdfSignatureInspector.EnsureRecognized(0, 0);
        PdfSignatureInspector.EnsureRecognized(1, 1);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfSignatureInspector.EnsureRecognized(0, 1));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfSignatureInspector.EnsureRecognized(0, 100));
        PdfSignatureInspector.EnsureRecognized(2, 2);
        PdfSignatureInspector.EnsureRecognized(2, 0); // direct/unindexed dictionary
        Assert.ThrowsExactly<InvalidDataException>(() =>
            PdfSignatureInspector.EnsureRecognized(1, 2));
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
