using Microsoft.VisualStudio.TestTools.UnitTesting;
using PdfSharp.Pdf;

namespace GetPdf;

[TestClass]
public sealed class PdfMarkupPolicyTests
{
    [TestMethod]
    public void UnrotatedUncroppedPageSupportsRectangle()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        Assert.IsTrue(PdfMarkupPolicy.CanPlace(page));
    }

    [TestMethod]
    public void RotatedPageDoesNotSupportRectangle()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Rotate = 90;
        Assert.IsFalse(PdfMarkupPolicy.CanPlace(page));
    }

    [TestMethod]
    public void CropBoxPageDoesNotSupportRectangle()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Elements["/CropBox"] = new PdfArray(doc);
        Assert.IsFalse(PdfMarkupPolicy.CanPlace(page));
    }

    [TestMethod]
    public void OffsetMediaBoxIsNotEligibleForOverlay()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XRect(10, 20, 400, 800));
        Assert.IsFalse(PdfMarkupPolicy.CanPlace(page));
    }

    [TestMethod]
    public void CustomUserUnitIsNotEligibleForOverlay()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Elements["/UserUnit"] = new PdfReal(2);
        Assert.IsFalse(PdfMarkupPolicy.CanPlace(page));
    }

    [TestMethod]
    public void NormalizedPointsMapProportionallyToPage()
    {
        var box = PdfMarkupPolicy.ToPoints(
            new PdfRectangleMarkup(.25, .5, .25, .1), 400, 800);
        Assert.AreEqual(100, box.X, .0001);
        Assert.AreEqual(400, box.Y, .0001);
        Assert.AreEqual(100, box.Width, .0001);
        Assert.AreEqual(80, box.Height, .0001);
    }
}
