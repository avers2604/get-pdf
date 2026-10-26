using GetDocument.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class DocumentHistoryTests
{
    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gdhist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void RemembersEveryOfficeFormatNewestFirstAndBounded()
    {
        string folder = NewFolder();
        try
        {
            var history = new DocumentHistory(folder);
            foreach (string ext in new[] { "pdf", "docx", "xlsx", "pptx", "png" })
                history.Remember(Path.Combine(folder, "doc." + ext));
            Assert.AreEqual("doc.png", Path.GetFileName(history.Recent()[0].Path));
            for (int i = 0; i < 30; i++) history.Remember(Path.Combine(folder, $"f{i}.pdf"));
            Assert.HasCount(DocumentHistory.MaxRecent, history.Recent());
            history.Remember(Path.Combine(folder, "f3.pdf"));
            Assert.AreEqual(1, history.Recent().Count(r => r.Path.EndsWith("f3.pdf")));
            Assert.ThrowsExactly<ArgumentException>(() => history.Remember(Path.Combine(folder, "virus.exe")));
            history.Forget(Path.Combine(folder, "f3.pdf"));
            Assert.IsFalse(history.Recent().Any(r => r.Path.EndsWith("f3.pdf")));
            history.ClearRecent();
            Assert.HasCount(0, history.Recent());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void FavouritesToggleAndSurviveDamagedFiles()
    {
        string folder = NewFolder();
        try
        {
            var history = new DocumentHistory(folder);
            string doc = Path.Combine(folder, "Смета.xlsx");
            Assert.IsTrue(history.ToggleFavorite(doc));
            Assert.IsTrue(history.IsFavorite(doc));
            Assert.IsFalse(history.ToggleFavorite(doc));
            File.WriteAllText(Path.Combine(folder, "favorites.json"), "{oops");
            Assert.HasCount(0, history.Favorites());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void ImportsGetPdfHistoryAndFavouritesOnce()
    {
        string folder = NewFolder(), legacy = NewFolder();
        try
        {
            string a = Path.Combine(legacy, "a.pdf"), b = Path.Combine(legacy, "b.pdf");
            File.WriteAllText(Path.Combine(legacy, "favorites.json"), System.Text.Json.JsonSerializer.Serialize(new[] { a }));
            File.WriteAllText(Path.Combine(legacy, "recent.json"),
                System.Text.Json.JsonSerializer.Serialize(new object[] { new { Path = b, OpenedUtc = "2026-10-23T09:30:00Z" }, a }));
            var history = new DocumentHistory(folder);
            history.ImportFromGetPdfOnce(legacy);
            Assert.IsTrue(history.IsFavorite(a));
            Assert.AreEqual(b, history.Recent()[0].Path);
            Assert.IsNotNull(history.Recent()[0].OpenedUtc);
            history.ToggleFavorite(a);                 // user removes it…
            history.ImportFromGetPdfOnce(legacy);      // …a second import must not bring it back
            Assert.IsFalse(history.IsFavorite(a));
        }
        finally { Directory.Delete(folder, recursive: true); Directory.Delete(legacy, recursive: true); }
    }
}
