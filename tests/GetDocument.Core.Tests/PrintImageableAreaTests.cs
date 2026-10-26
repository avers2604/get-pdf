using GetDocument.Core.Printing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Core.Tests;

[TestClass]
public sealed class PrintImageableAreaTests
{
    [TestMethod]
    public void UsesPrinterSpecificHardMarginsWhenValid()
    {
        var area = PrintImageableArea.Choose(793.7, 1122.52, 17, 25, 759, 1070);
        Assert.AreEqual(17d, area.X);
        Assert.AreEqual(25d, area.Y);
        Assert.AreEqual(759d, area.Width);
        Assert.AreEqual(1070d, area.Height);
    }

    [TestMethod]
    public void RejectsInvalidPrinterAreaAndKeepsSafeFallback()
    {
        foreach (var region in new[] {
            (X: -1d, Y: 2d, W: 700d, H: 900d),
            (X: 100d, Y: 2d, W: 750d, H: 900d),
            (X: 1d, Y: 1d, W: double.NaN, H: 800d),
            (X: 1d, Y: 1d, W: 0d, H: 800d) })
        {
            var area = PrintImageableArea.Choose(793.7, 1122.52,
                region.X, region.Y, region.W, region.H);
            Assert.AreEqual(18d, area.X);
            Assert.AreEqual(18d, area.Y);
            Assert.AreEqual(757.7, area.Width, 0.01);
        }
    }

    [TestMethod]
    public void TinyMediaSizeNeverHasNegativePrintArea()
    {
        var area = PrintImageableArea.Choose(20, 30, null, null, null, null);
        Assert.IsTrue(area.Width > 0);
        Assert.IsTrue(area.Height > 0);
        Assert.IsTrue(area.X + area.Width <= 20);
        Assert.IsTrue(area.Y + area.Height <= 30);
    }

    [TestMethod]
    public void InvalidMediaGeometryFailsClosed()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PrintImageableArea.Choose(-1, 100, 1, 1, 50, 50));
    }
}
