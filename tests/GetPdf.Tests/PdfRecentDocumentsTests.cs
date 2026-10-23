using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfRecentDocumentsTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "getpdf-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void RecentFilesDeduplicateAndBoundWithoutReadingDocumentContent()
    {
        string dir = NewDir();
        try
        {
            string store = Path.Combine(dir, "recents.json");
            for (int i = 0; i < 30; i++)
                PdfRecentDocuments.RememberIn(store, Path.Combine(dir, $"document-{i}.pdf"));
            PdfRecentDocuments.RememberIn(store, Path.Combine(dir, "document-21.pdf"));
            var result = PdfRecentDocuments.ReadFrom(store);
            Assert.HasCount(16, result);
            Assert.AreEqual(Path.Combine(dir, "document-21.pdf"), result[0].Path);
            Assert.AreEqual(1, result.Count(x => x.Path == result[0].Path));
            PdfRecentDocuments.ClearFrom(store);
            Assert.HasCount(0, PdfRecentDocuments.ReadFrom(store));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void RejectsNonPdfAndCorruptedHistory()
    {
        string dir = NewDir();
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

    [TestMethod]
    public void HistoryKeepsOpeningTimeReadsLegacyListAndForgetsOneEntry()
    {
        string dir = NewDir();
        try
        {
            string store = Path.Combine(dir, "recents.json");
            string a = Path.Combine(dir, "a.pdf"), b = Path.Combine(dir, "b.pdf");
            // 0.1.1 wrote a plain JSON array of paths.
            File.WriteAllText(store, System.Text.Json.JsonSerializer.Serialize(new[] { a, b }));
            var legacy = PdfRecentDocuments.ReadFrom(store);
            Assert.HasCount(2, legacy);
            Assert.IsNull(legacy[0].OpenedUtc);

            var opened = new DateTime(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);
            PdfRecentDocuments.RememberIn(store, b, opened);
            var updated = PdfRecentDocuments.ReadFrom(store);
            Assert.AreEqual(b, updated[0].Path);
            Assert.AreEqual(opened, updated[0].OpenedUtc);
            Assert.AreEqual(a, updated[1].Path);

            PdfRecentDocuments.ForgetIn(store, b.ToUpperInvariant());
            var left = PdfRecentDocuments.ReadFrom(store);
            Assert.HasCount(1, left);
            Assert.AreEqual(a, left[0].Path);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void FavoritesToggleAreBoundedAndIgnoreCase()
    {
        string dir = NewDir();
        try
        {
            string store = Path.Combine(dir, "favorites.json");
            string doc = Path.Combine(dir, "Приказ № 12.pdf");
            Assert.IsTrue(PdfFavoriteDocuments.ToggleIn(store, doc));
            Assert.IsTrue(PdfFavoriteDocuments.ContainsIn(store, doc.ToUpperInvariant()));
            Assert.IsFalse(PdfFavoriteDocuments.ToggleIn(store, doc.ToUpperInvariant()));
            Assert.HasCount(0, PdfFavoriteDocuments.ReadFrom(store));

            for (int i = 0; i < PdfFavoriteDocuments.MaxEntries; i++)
                PdfFavoriteDocuments.ToggleIn(store, Path.Combine(dir, $"f{i}.pdf"));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PdfFavoriteDocuments.ToggleIn(store, Path.Combine(dir, "one-more.pdf")));
            // Newest favourite first, and removal still works at the limit.
            Assert.AreEqual(Path.Combine(dir, $"f{PdfFavoriteDocuments.MaxEntries - 1}.pdf"),
                PdfFavoriteDocuments.ReadFrom(store)[0]);
            Assert.IsFalse(PdfFavoriteDocuments.ToggleIn(store, Path.Combine(dir, "f0.pdf")));
            Assert.ThrowsExactly<ArgumentException>(() =>
                PdfFavoriteDocuments.ToggleIn(store, Path.Combine(dir, "notes.docx")));
            File.WriteAllText(store, "{not json");
            Assert.HasCount(0, PdfFavoriteDocuments.ReadFrom(store));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void StartScreenDescribesWhenADocumentWasOpened()
    {
        var now = new DateTime(2026, 10, 23, 15, 0, 0, DateTimeKind.Local);
        Assert.StartsWith("сегодня, ", PdfStartItem.DescribeOpened(now.AddHours(-2).ToUniversalTime(), now));
        Assert.StartsWith("вчера, ", PdfStartItem.DescribeOpened(now.AddDays(-1).ToUniversalTime(), now));
        StringAssert.Contains(PdfStartItem.DescribeOpened(now.AddDays(-10).ToUniversalTime(), now), "окт");
        StringAssert.Contains(PdfStartItem.DescribeOpened(now.AddYears(-1).ToUniversalTime(), now), "2025");
    }
}
