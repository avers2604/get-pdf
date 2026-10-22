using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfNativeTextAnnotationWriterTests
{
    [TestMethod]
    public void WritesAnInteractiveTextCommentToNewPdfWithoutChangingOriginal()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-annotations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "with-comments.pdf");
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(source);
            }
            byte[] hashBefore = SHA256.HashData(File.ReadAllBytes(source));
            var comments = new[] {
                new PdfNativeAnnotationDraft("text", 0, .1, .2, .15, .25, "Комментарий ГЭТ")
            };
            PdfNativeTextAnnotationWriter.SaveNewCopy(source, output, comments);
            CollectionAssert.AreEqual(hashBefore, SHA256.HashData(File.ReadAllBytes(source)));
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.AreEqual(1, result.PageCount);
            var annots = result.Pages[0].Elements.GetObject("/Annots") as PdfArray;
            Assert.IsNotNull(annots);
            Assert.AreEqual(1, annots.Elements.Count);
            var reference = annots.Elements[0] as PdfReference;
            var annotation = reference?.Value as PdfDictionary ??
                annots.Elements[0] as PdfDictionary;
            Assert.IsNotNull(annotation);
            Assert.AreEqual("/Text", annotation.Elements.GetName("/Subtype"));
            Assert.AreEqual("Комментарий ГЭТ", annotation.Elements.GetString("/Contents"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void ExportsSquareAndHighlightWithNormalAppearancesAndQuadPoints()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-shapes-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "shapes.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.Save(source); }
            byte[] digest = SHA256.HashData(File.ReadAllBytes(source));
            PdfNativeTextAnnotationWriter.SaveNewCopy(source, output, [
                new("square", 0, .1, .1, .5, .4),
                new("highlight", 0, .15, .5, .7, .55)
            ]);
            CollectionAssert.AreEqual(digest, SHA256.HashData(File.ReadAllBytes(source)));
            using var checkedPdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            var annots = checkedPdf.Pages[0].Elements.GetObject("/Annots") as PdfArray;
            Assert.IsNotNull(annots);
            Assert.AreEqual(2, annots.Elements.Count);
            var square = annots.Elements[0] as PdfReference;
            var highlight = annots.Elements[1] as PdfReference;
            Assert.IsNotNull(square);
            Assert.IsNotNull(highlight);
            var sq = (PdfDictionary)square.Value;
            var hl = (PdfDictionary)highlight.Value;
            Assert.AreEqual("/Square", sq.Elements.GetName("/Subtype"));
            Assert.AreEqual("/Highlight", hl.Elements.GetName("/Subtype"));
            Assert.IsNotNull(sq.Elements.GetDictionary("/AP")?.Elements.GetObject("/N"));
            Assert.IsNotNull(hl.Elements.GetDictionary("/AP")?.Elements.GetObject("/N"));
            var quads = hl.Elements.GetObject("/QuadPoints") as PdfArray;
            Assert.IsNotNull(quads);
            Assert.AreEqual(8, quads.Elements.Count);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void SupportsMixedTextAndSquareHighlightOnSeparatePages()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-mixed-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "all.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.AddPage(); pdf.Save(source); }
            PdfNativeTextAnnotationWriter.SaveNewCopy(source, output, [
                new("text", 0, .1, .1, .15, .15, "Заметка"),
                new("square", 0, .25, .25, .45, .5),
                new("highlight", 1, .2, .2, .75, .25)
            ]);
            using var revised = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.AreEqual(2, revised.PageCount);
            Assert.AreEqual(2, ((PdfArray)revised.Pages[0].Elements.GetObject("/Annots")!).Elements.Count);
            Assert.AreEqual(1, ((PdfArray)revised.Pages[1].Elements.GetObject("/Annots")!).Elements.Count);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void RefusesUnsignedStructuralEditOnSignedOrInteractiveSource()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-annotations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string original = Path.Combine(folder, "form.pdf");
            string output = Path.Combine(folder, "new.pdf");
            using (var doc = new PdfDocument())
            {
                doc.AddPage();
                doc.Internals.Catalog.Elements.SetObject("/AcroForm", new PdfDictionary(doc));
                doc.Save(original);
            }
            var digest = SHA256.HashData(File.ReadAllBytes(original));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PdfNativeTextAnnotationWriter.SaveNewCopy(original, output, [
                    new("text", 0, .1, .1, .2, .2, "Note")
                ]));
            Assert.IsFalse(File.Exists(output));
            CollectionAssert.AreEqual(digest, SHA256.HashData(File.ReadAllBytes(original)));
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void NeverOverwriteSourceOrExistingOutputAndRejectsInvalidDraftKinds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-annotations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "already.pdf");
            using (var doc = new PdfDocument()) { doc.AddPage(); doc.Save(source); }
            File.WriteAllText(output, "Existing work must survive");
            var text = new[] { new PdfNativeAnnotationDraft("text", 0, .1, .1, .2, .2, "Note") };
            Assert.ThrowsExactly<IOException>(() =>
                PdfNativeTextAnnotationWriter.SaveNewCopy(source, source, text));
            Assert.ThrowsExactly<IOException>(() =>
                PdfNativeTextAnnotationWriter.SaveNewCopy(source, output, text));
            Assert.ThrowsExactly<NotSupportedException>(() =>
                PdfNativeTextAnnotationWriter.SaveNewCopy(source,
                    Path.Combine(folder, "unknown.pdf"), [
                        new("other", 0, .1, .1, .2, .2)
                    ]));
            Assert.AreEqual("Existing work must survive", File.ReadAllText(output));
            Assert.AreEqual(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void CancellationBeforePublishingLeavesNoPdfAndNoTempFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "getpdf-annotations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "new.pdf");
            using (var doc = new PdfDocument()) { doc.AddPage(); doc.Save(source); }
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                PdfNativeTextAnnotationWriter.SaveNewCopy(source, output, [
                    new("text", 0, .1, .1, .2, .2, "Note")
                ], cancel.Token));
            Assert.IsFalse(File.Exists(output));
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, true); }
    }
}
