using GetDocument.Office.EuroOfficeDesktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetDocument.Office.Tests;

[TestClass]
public sealed class EuroOfficeImeCaretGeometryTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Tab = Guid.NewGuid();

    [TestMethod]
    public void MapsVerifiedEditorCaretIntoUniformLetterboxedPreview()
    {
        var caret = new EuroOfficeImeCaretPosition(
            Session, Tab, 1, 1920, 1080, 960, 540, 2, 28);
        var rect = EuroOfficeImeCaretGeometry.MapToPreview(caret,
            640, 360, Session, Tab, 1);
        Assert.AreEqual(320, rect.X, .001);
        Assert.AreEqual(180, rect.Y, .001);
        Assert.AreEqual(1, rect.Width, .001);
        Assert.AreEqual(9.334, rect.Height, .01);
    }

    [TestMethod]
    public void RejectsCrossTabReplayAndOutOfRangeCaret()
    {
        var caret = new EuroOfficeImeCaretPosition(
            Session, Tab, 3, 1000, 800, 600, 300, 2, 20);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeImeCaretGeometry.MapToPreview(caret, 500, 500,
                Session, Guid.NewGuid(), 3));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            EuroOfficeImeCaretGeometry.MapToPreview(caret, 500, 500,
                Session, Tab, 4));
        foreach (var invalid in new[] {
            new EuroOfficeImeCaretPosition(Session, Tab, 1, 1000, 800, -5, 10, 1, 20),
            new EuroOfficeImeCaretPosition(Session, Tab, 1, 1000, 800, 1001, 10, 1, 20),
            new EuroOfficeImeCaretPosition(Session, Tab, 1, 1000, 800, 2, 10, 1, 2000)
        })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeImeCaretGeometry.MapToPreview(invalid, 640, 360,
                    Session, Tab, 1));
    }

    [TestMethod]
    public void RejectsNaNDpiExtremeAndMissingVisual()
    {
        var caret = new EuroOfficeImeCaretPosition(
            Session, Tab, 1, 1024, 768, 20, 30, 2, 20);
        foreach (var invalidWidth in new[] { 0.0, -1, double.NaN, 1000000.0 })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                EuroOfficeImeCaretGeometry.MapToPreview(caret, invalidWidth,
                    300, Session, Tab, 1));
    }

    [TestMethod]
    public void UnsupportedWindowsAndX11CannotBePretendedToUseCefOsr()
    {
        Assert.IsFalse(EuroOfficeCefOsrPlatformPolicy.IsSupported(
            operatingSystemLinux: false, qpaPlatform: "windows"));
        Assert.IsFalse(EuroOfficeCefOsrPlatformPolicy.IsSupported(
            operatingSystemLinux: true, qpaPlatform: "xcb"));
        Assert.IsFalse(EuroOfficeCefOsrPlatformPolicy.IsSupported(
            operatingSystemLinux: true, qpaPlatform: "offscreen"));
        Assert.IsTrue(EuroOfficeCefOsrPlatformPolicy.IsSupported(
            operatingSystemLinux: true, qpaPlatform: "wayland"));
        Assert.AreEqual("LibreOffice/UNO",
            EuroOfficeCefOsrPlatformPolicy.ProductionFallback);
    }
}
