using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfSpreadScrollRegressionTests
{
    [TestMethod]
    public void ScrollingInSpreadRetainsExplicitRightHandPage()
    {
        Rect[] row = DocumentViewportLayout.MeasureSpread(
            [new Size(600, 800), new Size(600, 900),
             new Size(600, 800), new Size(600, 800)], 1.0, 24);
        Assert.AreEqual(1, DocumentViewportLayout.PageAtVerticalOffset(row, row[1].Top + 100, 1));
        Assert.AreEqual(0, DocumentViewportLayout.PageAtVerticalOffset(row, row[1].Top + 100, 0));
        Assert.AreEqual(2, DocumentViewportLayout.PageAtVerticalOffset(row, row[2].Top + 100, 1));
    }

    [TestMethod]
    public void EmptyAndBelowContentOffsetsRemainBounded()
    {
        Assert.AreEqual(-1, DocumentViewportLayout.PageAtVerticalOffset([], 100, 0));
        var bounds = DocumentViewportLayout.MeasureSpread(
            [new Size(120, 200), new Size(120, 200)], 1, 16);
        Assert.AreEqual(0, DocumentViewportLayout.PageAtVerticalOffset(bounds, -100, 0));
        Assert.AreEqual(0, DocumentViewportLayout.PageAtVerticalOffset(bounds, 1000, 1));
    }
}
