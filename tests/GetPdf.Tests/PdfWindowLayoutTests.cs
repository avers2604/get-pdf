using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfWindowLayoutTests
{
    [TestMethod]
    public void NarrowScaledDesktopLowersMinimumsAndPreservesVisibleWindow()
    {
        var bounds = DocumentWindowLayout.Calculate(
            new Rect(0, 0, 870, 590), 300, 100,
            1440, 950, 950, 650);
        Assert.AreEqual(870.0, bounds.Width);
        Assert.AreEqual(590.0, bounds.Height);
        Assert.AreEqual(870.0, bounds.MinWidth);
        Assert.AreEqual(590.0, bounds.MinHeight);
        Assert.AreEqual(0.0, bounds.Left);
        Assert.AreEqual(0.0, bounds.Top);
    }

    [TestMethod]
    public void OffScreenWindowIsClampedToWorkAreaIncludingNegativeMonitorCoordinates()
    {
        var bounds = DocumentWindowLayout.Calculate(
            new Rect(-1600, -100, 1400, 900), -2400, 1000,
            1000, 700, 950, 650);
        Assert.AreEqual(-1600.0, bounds.Left);
        Assert.AreEqual(100.0, bounds.Top);
        Assert.AreEqual(1000.0, bounds.Width);
        Assert.AreEqual(700.0, bounds.Height);
    }

    [TestMethod]
    public void AutoWindowCoordinatesAreCenteredInsideAvailableArea()
    {
        var bounds = DocumentWindowLayout.Calculate(
            new Rect(100, 200, 1600, 1000),
            double.NaN, double.NaN, 1000, 800, 950, 650);
        Assert.AreEqual(400.0, bounds.Left);
        Assert.AreEqual(300.0, bounds.Top);
    }

    [TestMethod]
    public void InvalidWorkAreaIsRejectedBeforeWindowMutation()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DocumentWindowLayout.Calculate(new Rect(0, 0, 0, 100),
                0, 0, 950, 650, 950, 650));
    }
}
