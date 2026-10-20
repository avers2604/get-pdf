using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GetPdf.Tests;

[TestClass]
public sealed class PdfNativeGdiPrinterTests
{
    [TestMethod]
    public void AspectRatioPreservedWithinPrinterArea()
    {
        var rect = PdfNativeGdiPrintLayout.Fit(595, 842, 4960, 7016, 100, 300);
        Assert.IsTrue(rect.Width > 0 && rect.Height > 0);
        Assert.IsTrue(rect.X >= 0 && rect.Y >= 0);
        Assert.IsTrue(rect.X + rect.Width <= 4960);
        Assert.IsTrue(rect.Y + rect.Height <= 7016);
        Assert.IsTrue(Math.Abs((double)rect.Width / rect.Height - 595.0 / 842) < .003);
    }

    [TestMethod]
    public void InvalidPageGeometryRefusedBeforeSpooling()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfNativeGdiPrintLayout.Fit(0, 842, 2000, 3000, 0, 300));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfNativeGdiPrintLayout.Fit(595, 842, 2000, 3000, -1, 300));
    }

    [TestMethod]
    public void NativeLayoutRefusesMarginsLargerThanDriverArea()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfNativeGdiPrintLayout.Fit(595, 842, 200, 300, 100, 300));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfNativeGdiPrintLayout.Fit(595, 842, 200, 300, 150, 300));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PdfNativeGdiPrintLayout.Fit(595, 842, 200, 300, int.MaxValue, 300));
    }

    [TestMethod]
    public void NativeLayoutNeverViolatesRequestedMargins()
    {
        var box = PdfNativeGdiPrintLayout.Fit(842, 595, 4000, 3000, 150, 300);
        Assert.IsTrue(box.X >= 150 && box.Y >= 150);
        Assert.IsTrue(box.X + box.Width <= 4000 - 150);
        Assert.IsTrue(box.Y + box.Height <= 3000 - 150);
    }

    [TestMethod]
    public void NativeGdiDisabledUnlessItPolicyAllows()
    {
        var no = WorkstationPolicy.Parse("{\"Version\":1}");
        Assert.IsFalse(no.AllowNativeGdiPrinting);
        var yes = WorkstationPolicy.Parse("{\"Version\":1,\"AllowNativeGdiPrinting\":true}");
        Assert.IsTrue(yes.AllowNativeGdiPrinting);
    }

    [TestMethod]
    public void NativeGdiRefusesPagesWithUnsavedOverlays()
    {
        Assert.IsTrue(PdfNativeGdiPrintBackend.IsSafeSourceSelection(
            [new PdfPageReference(0, 0), new PdfPageReference(1, 0)]));
        Assert.IsFalse(PdfNativeGdiPrintBackend.IsSafeSourceSelection(
            [new PdfPageReference(0, 90)]));
        Assert.IsFalse(PdfNativeGdiPrintBackend.IsSafeSourceSelection(
            [new PdfPageReference(0, 0, Stamp: "УТВЕРЖДАЮ")]));
        Assert.IsFalse(PdfNativeGdiPrintBackend.IsSafeSourceSelection([]));
    }
}
