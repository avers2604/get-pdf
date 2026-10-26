using GetDocument.Office.LibreOffice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class LibreOfficeRuntimeTests
{
    [TestMethod]
    public void HandlesOfficeAndOpenDocumentFormatsButNotPdfOrImages()
    {
        foreach (string name in new[] { "a.docx", "a.DOC", "a.odt", "a.rtf", "b.xlsx", "b.xls", "b.ods", "b.csv",
                                        "c.pptx", "c.ppt", "c.odp", "c.ppsx" })
            Assert.IsTrue(LibreOfficeRuntime.Handles(name), name);
        foreach (string name in new[] { "a.pdf", "a.png", "a.txt", "a.exe", "noext" })
            Assert.IsFalse(LibreOfficeRuntime.Handles(name), name);
    }

    [TestMethod]
    public void PreviewCacheKeyChangesWhenTheDocumentIsSaved()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gdlo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string doc = Path.Combine(folder, "Записка.docx");
            File.WriteAllText(doc, "v1");
            File.SetLastWriteTimeUtc(doc, new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc));
            string first = LibreOfficeRuntime.PreviewCachePath(doc);
            Assert.AreEqual(first, LibreOfficeRuntime.PreviewCachePath(doc), "same file, same key");
            File.SetLastWriteTimeUtc(doc, new DateTime(2026, 10, 26, 9, 5, 0, DateTimeKind.Utc));
            Assert.AreNotEqual(first, LibreOfficeRuntime.PreviewCachePath(doc), "a save must give a fresh preview");
            StringAssert.EndsWith(first, ".pdf");
            Assert.IsFalse(Path.GetFileName(first).Contains("Записка"), "cache names never reveal document titles");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public async Task ConvertsARealDocxToPdfWhenLibreOfficeIsInstalled()
    {
        if (!LibreOfficeRuntime.IsAvailable)
            Assert.Inconclusive("LibreOffice не установлен на этой машине (GETDOC_SOFFICE не задан).");
        string folder = Path.Combine(Path.GetTempPath(), "gdlo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string doc = Path.Combine(folder, "Отчёт.docx");
            OfficeDocumentFactory.CreateNew(GetDocument.Core.Workspace.WorkspaceDocumentKind.Text, doc);
            string pdf = await LibreOfficeRuntime.ConvertToPdfAsync(doc);
            byte[] head = new byte[5];
            await using (var stream = File.OpenRead(pdf)) await stream.ReadExactlyAsync(head);
            Assert.AreEqual("%PDF-", System.Text.Encoding.ASCII.GetString(head));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
