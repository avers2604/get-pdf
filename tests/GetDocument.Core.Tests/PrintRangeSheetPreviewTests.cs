using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintRangeSheetPreviewTests
{
    [TestMethod]
    public void FirstPreviewSheetUsesSelectedPdfPagesNotDocumentStart()
    {
        var selected = new[] { 12, 14, 16 };
        var sheet = PrintSheetPreviewPlan.Create(20, selected,
            PrintPagesPerSheet.One, PrintOrientation.Portrait);
        CollectionAssert.AreEqual(new[] { 12 }, sheet.PageNumbers.ToArray());
        Assert.AreEqual(3, sheet.TotalSheets);
        Assert.AreEqual(0, sheet.SheetIndex);
    }

    [TestMethod]
    public void NextSheetShowsActualPageEvenAcrossGaps()
    {
        var selected = new[] { 2, 8, 11, 19, 20 };
        var second = PrintSheetPreviewPlan.Create(20, selected,
            PrintPagesPerSheet.Two, PrintOrientation.Landscape, sheetIndex: 1);
        Assert.AreEqual(1, second.Rows);
        Assert.AreEqual(2, second.Columns);
        CollectionAssert.AreEqual(new[] { 11, 19 }, second.PageNumbers.ToArray());
        Assert.AreEqual(3, second.TotalSheets);
    }

    [TestMethod]
    public void LastFourUpSheetDoesNotInventBlankContent()
    {
        var sheet = PrintSheetPreviewPlan.Create(9,
            Enumerable.Range(1, 9).ToArray(),
            PrintPagesPerSheet.Four, PrintOrientation.Automatic, sheetIndex: 2);
        CollectionAssert.AreEqual(new[] { 9 }, sheet.PageNumbers.ToArray());
        Assert.AreEqual(3, sheet.TotalSheets);
    }

    [TestMethod]
    public void EmptyDuplicateOutOfOrderOrOutOfDocumentSelectionIsRejected()
    {
        foreach (var pages in new[] { Array.Empty<int>(), new[] { 1, 1 },
                     new[] { 4, 2 }, new[] { 0 }, new[] { 11 } })
            Assert.ThrowsExactly<ArgumentException>(() =>
                PrintSheetPreviewPlan.Create(10, pages,
                    PrintPagesPerSheet.One, PrintOrientation.Portrait));
    }

    [TestMethod]
    public void InvalidSheetIndexIsRejectedWithoutStaleThumbnail()
    {
        foreach (int index in new[] { -1, 3, int.MaxValue })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                PrintSheetPreviewPlan.Create(5, new[] { 2, 3, 5 },
                    PrintPagesPerSheet.One, PrintOrientation.Portrait,
                    sheetIndex: index));
    }

    [TestMethod]
    public void EstimatedDuplexPhysicalSheetsAreRoundedPerCopy()
    {
        var plan = PrintJobPlan.Create("Printer", 3, "", 2,
            PrintDuplex.LongEdge, PrintPaper.A4);
        Assert.AreEqual(6, plan.SheetSidesToPrint);
        Assert.AreEqual(4, plan.EstimatedPhysicalSheets);
        var singleSided = PrintJobPlan.Create("Printer", 3, "", 2,
            PrintDuplex.OneSided, PrintPaper.A4);
        Assert.AreEqual(6, singleSided.EstimatedPhysicalSheets);
    }
}
