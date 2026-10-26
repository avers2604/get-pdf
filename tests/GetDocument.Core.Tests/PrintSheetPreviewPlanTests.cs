using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintSheetPreviewPlanTests
{
    [TestMethod]
    public void PortraitTwoUpStacksTwoRealPageSlots()
    {
        var layout = PrintSheetPreviewPlan.Create(6, PrintPagesPerSheet.Two, PrintOrientation.Portrait);
        Assert.AreEqual(2, layout.Rows);
        Assert.AreEqual(1, layout.Columns);
        CollectionAssert.AreEqual(new[] { 1, 2 }, layout.PageNumbers.ToArray());
    }

    [TestMethod]
    public void LandscapeTwoUpUsesSideBySideSlots()
    {
        var layout = PrintSheetPreviewPlan.Create(4, PrintPagesPerSheet.Two, PrintOrientation.Landscape);
        Assert.AreEqual(1, layout.Rows);
        Assert.AreEqual(2, layout.Columns);
    }

    [TestMethod]
    public void FourUpShowsFourPagesWithoutInventingFifth()
    {
        var layout = PrintSheetPreviewPlan.Create(3, PrintPagesPerSheet.Four, PrintOrientation.Automatic);
        Assert.AreEqual(2, layout.Rows);
        Assert.AreEqual(2, layout.Columns);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, layout.PageNumbers.ToArray());
    }

    [TestMethod]
    public void OnePageDocumentAlwaysDisplaysOnlyOneRealPage()
    {
        var layout = PrintSheetPreviewPlan.Create(1, PrintPagesPerSheet.Four, PrintOrientation.Automatic);
        CollectionAssert.AreEqual(new[] { 1 }, layout.PageNumbers.ToArray());
    }

    [TestMethod]
    public void BadInputCannotDriveUnboundedPdfThumbnails()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PrintSheetPreviewPlan.Create(1001, PrintPagesPerSheet.One, PrintOrientation.Portrait));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PrintSheetPreviewPlan.Create(0, PrintPagesPerSheet.One, PrintOrientation.Portrait));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PrintSheetPreviewPlan.Create(3, (PrintPagesPerSheet)16, PrintOrientation.Portrait));
    }
}
