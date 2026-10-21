using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfOutputVerifierTests
{
    [TestMethod]
    public void DamagedPdfIsRejectedRatherThanAcceptedAsSavedResult()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string broken = Path.Combine(folder, "broken.pdf");
            byte[] bytes = "%PDF-1.7\nNOT_A_VALID_DOCUMENT\n"u8.ToArray();
            File.WriteAllBytes(broken, bytes);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PdfOutputVerifier.Verify(broken, 1));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(broken));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ValidPdfWithWrongPageCountIsRejected()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "one-page.pdf");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(file);
            }
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PdfOutputVerifier.Verify(file, 2));
            PdfOutputVerifier.Verify(file, 1);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void MissingOrEmptyPdfCannotBePublished()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "missing.pdf");
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PdfOutputVerifier.Verify(path, 1));
            File.WriteAllBytes(path, []);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PdfOutputVerifier.Verify(path, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                PdfOutputVerifier.Verify(path, 0));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
