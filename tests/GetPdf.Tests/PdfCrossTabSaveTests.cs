using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfCrossTabSaveTests
{
    [TestMethod]
    public void MixedSourceWriterCreatesCopyWithoutModifyingEitherOriginal()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var a = Path.Combine(folder, "a.pdf");
            var b = Path.Combine(folder, "b.pdf");
            var output = Path.Combine(folder, "result.pdf");
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.AddPage();
                document.Save(a);
            }
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.Save(b);
            }
            var beforeA = File.ReadAllBytes(a);
            var beforeB = File.ReadAllBytes(b);
            var plan = PdfCrossTabPageTransfer.Build(a,
                [new PdfPageReference(0, 0), new PdfPageReference(1, 0)],
                0, 2, b, new PdfPageReference(0, 0), 1, 20);
            PdfDocumentWriter.SaveMixed(output, plan);
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.AreEqual(3, result.PageCount);
            CollectionAssert.AreEqual(beforeA, File.ReadAllBytes(a));
            CollectionAssert.AreEqual(beforeB, File.ReadAllBytes(b));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
