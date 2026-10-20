using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfRecentDocumentsTests
{
    [TestMethod]
    public void RecentFilesDeduplicateAndBoundWithoutReadingDocumentContent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "getpdf-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string store = Path.Combine(dir, "recents.json");
            for (int i = 0; i < 30; i++)
                PdfRecentDocuments.RememberIn(store, Path.Combine(dir, $"document-{i}.pdf"));
            PdfRecentDocuments.RememberIn(store, Path.Combine(dir, "document-21.pdf"));
            var result = PdfRecentDocuments.ReadFrom(store);
            Assert.HasCount(16, result);
            Assert.AreEqual(Path.Combine(dir, "document-21.pdf"), result[0]);
            Assert.AreEqual(1, result.Count(x => x == result[0]));
            PdfRecentDocuments.ClearFrom(store);
            Assert.HasCount(0, PdfRecentDocuments.ReadFrom(store));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void RejectsNonPdfAndCorruptedHistory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "getpdf-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string store = Path.Combine(dir, "recents.json");
            Assert.ThrowsExactly<ArgumentException>(() =>
                PdfRecentDocuments.RememberIn(store, Path.Combine(dir, "private.txt")));
            File.WriteAllText(store, "broken json");
            Assert.HasCount(0, PdfRecentDocuments.ReadFrom(store));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}