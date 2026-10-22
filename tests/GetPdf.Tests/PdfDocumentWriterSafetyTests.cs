using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfDocumentWriterSafetyTests
{
    [TestMethod]
    public void BareRelativeOutputPathWritesVerifiedCopyWithoutChangingSource()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-atomic-" + Guid.NewGuid().ToString("N"));
        string relative = "getpdf-atomic-" + Guid.NewGuid().ToString("N") + ".pdf";
        string output = Path.GetFullPath(relative);
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "worm-original.pdf");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(source);
            }
            byte[] before = SHA256.HashData(File.ReadAllBytes(source));
            PdfDocumentWriter.SaveAs(source, relative,
                [new PdfPageReference(0, 0)]);

            Assert.IsTrue(File.Exists(output));
            using (var result = PdfReader.Open(output, PdfDocumentOpenMode.Import))
                Assert.AreEqual(1, result.PageCount);
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void InteractiveAcroFormNeverReachesPageRewrite()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-form-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "protected.pdf");
            string output = Path.Combine(folder, "changed.pdf");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                var form = new PdfSharp.Pdf.PdfDictionary(pdf);
                pdf.Internals.Catalog.Elements.SetObject("/AcroForm", form);
                pdf.Save(source);
            }
            byte[] before = SHA256.HashData(File.ReadAllBytes(source));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PdfDocumentWriter.SaveAs(source, output, [new PdfPageReference(0, 0)]));
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.IsFalse(File.Exists(output));
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void AnnotationBearingPdfIsNeverSilentlyFlattenedByPageRewrite()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-annot-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "annotated.pdf");
            string output = Path.Combine(folder, "changed.pdf");
            using (var pdf = new PdfDocument())
            {
                var page = pdf.AddPage();
                page.Elements.SetObject("/Annots",
                    new PdfSharp.Pdf.PdfArray(pdf));
                pdf.Save(source);
            }
            byte[] before = SHA256.HashData(File.ReadAllBytes(source));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PdfDocumentWriter.SaveAs(source, output, [new PdfPageReference(0, 0)]));
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.IsFalse(File.Exists(output));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ExistingOutputIsNeverReplacedByPdfPageWriter()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-no-overwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string destination = Path.Combine(folder, "existing.pdf");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(source);
            }
            byte[] before = File.ReadAllBytes(source);
            byte[] existing = "keep unrelated file intact"u8.ToArray();
            File.WriteAllBytes(destination, existing);

            Assert.ThrowsExactly<IOException>(() =>
                PdfDocumentWriter.SaveAs(source, destination,
                    [new PdfPageReference(0, 0)]));

            CollectionAssert.AreEqual(before, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(existing, File.ReadAllBytes(destination));
            Assert.AreEqual(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
