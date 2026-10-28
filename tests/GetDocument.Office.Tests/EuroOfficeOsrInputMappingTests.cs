using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeOsrInputMappingTests
{
    [DataTestMethod]
    [DataRow(1.0, 100, 50, 100, 50)]
    [DataRow(1.25, 100, 50, 125, 62)]
    [DataRow(1.5, 100, 50, 150, 75)]
    [DataRow(2.0, 100, 50, 200, 100)]
    public void MapsLogicalAvaloniaCoordinatesIntoPhysicalWaylandCefPixels(
        double dpiScale, int x, int y, int expectedX, int expectedY)
    {
        var mapped = EuroOfficeOsrInputMapping.MapPointer(
            x, y, 800, 600, dpiScale);
        Assert.AreEqual(expectedX, mapped.PhysicalX);
        Assert.AreEqual(expectedY, mapped.PhysicalY);
        Assert.AreEqual((int)Math.Floor(800 * dpiScale), mapped.ViewPhysicalWidth);
        Assert.AreEqual((int)Math.Floor(600 * dpiScale), mapped.ViewPhysicalHeight);
        Assert.IsFalse(mapped.AuthorizedToForward);
    }

    [TestMethod]
    public void RejectsPointerOutsideViewOrNaNAndUnreasonableDpi()
    {
        foreach ((double x, double y, double scale) in new[]
        {
            (-1d, 0d, 1d), (800d, 0d, 1d), (0d, 600d, 1d),
            (double.NaN, 1d, 1d), (1d, double.PositiveInfinity, 1d),
            (1d, 2d, 0d), (1d, 2d, 5d)
        })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                EuroOfficeOsrInputMapping.MapPointer(x, y, 800, 600, scale));
    }

    [TestMethod]
    public void InputMappingCannotGrantKeyboardOrImeOrClipboardRights()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeOsrInputMapping.AuthorizeRemoteKeyboardOrIme());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            EuroOfficeOsrInputMapping.AuthorizeClipboardBridge());
    }
}
