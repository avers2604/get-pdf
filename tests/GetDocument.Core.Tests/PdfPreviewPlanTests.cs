using GetDocument.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PdfPreviewPlanTests
{
    [TestMethod]
    public void PageSizeIsBoundedWithoutDistortingAspectRatio()
    {
        var size = PdfPreviewPlan.GetRasterSize(612, 792);
        Assert.IsTrue(size.Width > 0 && size.Height > 0);
        Assert.IsTrue(size.Width <= 1440 && size.Height <= 1440);
        Assert.AreEqual(612.0 / 792, (double)size.Width / size.Height, 0.002);
    }

    [TestMethod]
    public void ExtremelyWidePagesAlsoFitBudget()
    {
        var size = PdfPreviewPlan.GetRasterSize(20000, 100);
        Assert.IsTrue(size.Width <= 1440 && size.Height <= 1440);
    }

    [TestMethod]
    public void RejectsMaliciousOrInvalidPageDimensions()
    {
        foreach (var width in new[] { 0, -1, double.NaN, double.PositiveInfinity, 20001.0 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PdfPreviewPlan.GetRasterSize(width, 792));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PdfPreviewPlan.GetRasterSize(612, 0));
    }

    [TestMethod]
    public void RefusesOversizedPdfsAndInvalidPages()
    {
        PdfPreviewPlan.ValidateInputLength(16 * 1024);
        Assert.ThrowsExactly<InvalidDataException>(() => PdfPreviewPlan.ValidateInputLength(0));
        Assert.ThrowsExactly<InvalidDataException>(
            () => PdfPreviewPlan.ValidateInputLength(129L * 1024 * 1024));
        PdfPreviewPlan.ValidatePage(0, 1);
        PdfPreviewPlan.ValidatePage(999, 1000);
        Assert.ThrowsExactly<InvalidDataException>(() => PdfPreviewPlan.ValidatePage(0, 0));
        Assert.ThrowsExactly<InvalidDataException>(() => PdfPreviewPlan.ValidatePage(0, 1001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfPreviewPlan.ValidatePage(-1, 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfPreviewPlan.ValidatePage(3, 3));
    }

    [TestMethod]
    public void RenderPixelBudgetCanBeReduced()
    {
        var size = PdfPreviewPlan.GetRasterSize(612, 792, 800);
        Assert.IsTrue(size.Width <= 800 && size.Height <= 800);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfPreviewPlan.GetRasterSize(612, 792, 5000));
    }
}
