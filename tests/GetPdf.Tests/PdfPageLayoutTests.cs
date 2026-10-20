using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfPageLayoutTests
{
    [TestMethod]
    public void SpreadPositionsPagesInPairsWithoutOverlaps()
    {
        Size[] pages = [new(500, 900), new(600, 800), new(500, 700)];
        var rect = DocumentViewportLayout.MeasureSpread(pages, 1, 24);
        Assert.HasCount(3, rect);
        Assert.IsTrue(rect[1].Left > rect[0].Right);
        Assert.IsTrue(rect[2].Top >= rect[0].Bottom + 24);
        Assert.IsTrue(rect[2].Top >= rect[1].Bottom + 24);
    }

    [TestMethod]
    public void SpreadRejectsInvalidSizes()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            DocumentViewportLayout.MeasureSpread([new Size(0, 100)], 1, 24));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DocumentViewportLayout.MeasureSpread([new Size(100, 100)], 0, 24));
    }

    [TestMethod]
    public void BookmarkRoundtripIsBoundedAndSorted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "getpdf-bookmarks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "one.json");
            Assert.IsTrue(PdfPageBookmarks.ToggleIn(path, 8));
            Assert.IsTrue(PdfPageBookmarks.ToggleIn(path, 2));
            CollectionAssert.AreEqual(new[] { 2, 8 }, PdfPageBookmarks.ReadFrom(path).ToArray());
            Assert.IsFalse(PdfPageBookmarks.ToggleIn(path, 8));
            CollectionAssert.AreEqual(new[] { 2 }, PdfPageBookmarks.ReadFrom(path).ToArray());
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfPageBookmarks.ToggleIn(path, -1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}