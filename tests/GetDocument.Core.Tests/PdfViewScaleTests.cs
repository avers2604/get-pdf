using GetDocument.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PdfViewScaleTests
{
    [TestMethod]
    public void DefaultScaleIsOneHundredPercent()
    {
        Assert.AreEqual(100, PdfViewScale.Percent(PdfViewScale.DefaultStep));
        var size = PdfViewScale.GetDisplaySize(1440, 1000, PdfViewScale.DefaultStep);
        Assert.AreEqual(893, size.Width);
        Assert.AreEqual(620, size.Height);
    }

    [TestMethod]
    public void ControlsAreBoundedAndReversible()
    {
        Assert.AreEqual(3, PdfViewScale.Move(PdfViewScale.DefaultStep, 1));
        Assert.AreEqual(PdfViewScale.DefaultStep,
            PdfViewScale.Move(PdfViewScale.Move(PdfViewScale.DefaultStep, 1), -1));
        Assert.AreEqual(0, PdfViewScale.Move(0, -100));
        Assert.AreEqual(PdfViewScale.StepCount - 1,
            PdfViewScale.Move(PdfViewScale.StepCount - 1, 100));
    }

    [TestMethod]
    public void LargerScaleIncreasesBothDimensionsWithoutChangingRatio()
    {
        var small = PdfViewScale.GetDisplaySize(1000, 750, 0);
        var large = PdfViewScale.GetDisplaySize(1000, 750, PdfViewScale.StepCount - 1);
        Assert.IsTrue(large.Width > small.Width);
        Assert.IsTrue(large.Height > small.Height);
        Assert.AreEqual(1000.0 / 750.0, (double)large.Width / large.Height, 0.01);
    }

    [TestMethod]
    public void InvalidScalesAndRasterDimensionsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfViewScale.Percent(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PdfViewScale.Move(5, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfViewScale.GetDisplaySize(0, 500, PdfViewScale.DefaultStep));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfViewScale.GetDisplaySize(500, 2500, PdfViewScale.DefaultStep));
    }
}
