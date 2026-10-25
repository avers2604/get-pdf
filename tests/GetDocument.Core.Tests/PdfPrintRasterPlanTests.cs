using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PdfPrintRasterPlanTests
{
    [TestMethod]
    public void A4PrintIsSharperThan1440PixelScreenPreview()
    {
        var size = PdfPrintRasterPlan.GetRasterSize(612, 792);
        Assert.IsTrue(size.Width > 1440 && size.Height > 1440);
        Assert.IsTrue((long)size.Width * size.Height <= PdfPrintRasterPlan.MaxPixels);
        Assert.AreEqual(612d / 792d, (double)size.Width / size.Height, 0.003);
    }

    [TestMethod]
    public void VeryWideOrLongPagesRespectHardPixelLimits()
    {
        foreach (var dimensions in new[] { (20000d, 1d), (1d, 20000d), (4000d, 5000d) })
        {
            var size = PdfPrintRasterPlan.GetRasterSize(dimensions.Item1, dimensions.Item2);
            Assert.IsTrue(size.Width >= 1 && size.Width <= PdfPrintRasterPlan.MaxLongSide);
            Assert.IsTrue(size.Height >= 1 && size.Height <= PdfPrintRasterPlan.MaxLongSide);
            Assert.IsTrue((long)size.Width * size.Height <= PdfPrintRasterPlan.MaxPixels);
        }
    }

    [TestMethod]
    public void InvalidDimensionsNeverReachPdfium()
    {
        foreach (var value in new[] { -1, 0, double.NaN, double.PositiveInfinity, 20001d })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                PdfPrintRasterPlan.GetRasterSize(value, 792));
    }
}
