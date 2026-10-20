using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfCrossTabDraftPipelineTests
{
    [TestMethod]
    public void AppendThenUndoRedoProducesExpectedSeparateExportsAndKeepsOriginals()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "getpdf-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string a = Path.Combine(folder, "destination.pdf");
            string b = Path.Combine(folder, "donor.pdf");
            string one = Path.Combine(folder, "one.pdf");
            string master = Path.Combine(folder, "text-master-import.pdf");
            string result = Path.Combine(folder, "result.pdf");
            string undone = Path.Combine(folder, "undone.pdf");

            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.AddPage();
                pdf.Save(a);
            }
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage();
                pdf.Save(b);
            }
            byte[] originalA = File.ReadAllBytes(a);
            byte[] originalB = File.ReadAllBytes(b);

            PdfDocumentWriter.SaveAs(b, one, [new PdfPageReference(0, 0)]);
            PdfDocumentWriter.AppendSourceRevision(a, one, master);
            PdfOutputVerifier.Verify(master, 3);

            var plan = new PdfPagePlan(2);
            var history = new PdfEditHistory(plan);
            int insertedSource = plan.RegisterSourcePage();
            history.Execute(p => p.InsertSourcePageAfter(0, insertedSource));
            Assert.HasCount(3, plan.Pages);
            Assert.AreEqual(2, plan.Pages[1].SourcePage);

            PdfDocumentWriter.SaveAs(master, result, plan.Pages);
            using (var pdf = PdfReader.Open(result, PdfDocumentOpenMode.Import))
                Assert.AreEqual(3, pdf.PageCount);

            Assert.IsTrue(history.Undo());
            PdfDocumentWriter.SaveAs(master, undone, plan.Pages);
            using (var pdf = PdfReader.Open(undone, PdfDocumentOpenMode.Import))
                Assert.AreEqual(2, pdf.PageCount);

            Assert.IsTrue(history.Redo());
            Assert.HasCount(3, plan.Pages);
            Assert.AreEqual(2, plan.Pages[1].SourcePage);
            CollectionAssert.AreEqual(originalA, File.ReadAllBytes(a));
            CollectionAssert.AreEqual(originalB, File.ReadAllBytes(b));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void RecoveryManifestRetainsAppendOnlyImportedPages()
    {
        var plan = new PdfPagePlan(2);
        int added = plan.RegisterSourcePage();
        plan.InsertSourcePageAfter(1, added);
        var manifest = new PdfDraftStore.Manifest(
            Version: 2,
            OriginalPath: Path.Combine(Path.GetTempPath(), "original.pdf"),
            SourceSha256: new string('A', 64),
            SourcePageCount: 3,
            Pages: plan.Snapshot(),
            SelectedPage: 2, Zoom: 1.0,
            OriginalPageCount: 2,
            WorkingSourceFile: "text-master-01234567.pdf",
            WorkingSourceHash: new string('B', 64));
        var restored = PdfDraftStore.Validate(manifest, maxPages: 1000);
        Assert.HasCount(3, restored.Pages);
        Assert.AreEqual(3, restored.SourcePageCount);
        Assert.AreEqual(2, restored.Pages[2].SourcePage);
    }
}
